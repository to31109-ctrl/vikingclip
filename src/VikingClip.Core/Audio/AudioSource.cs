using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;
using VikingClip.Core.Logging;
using VikingClip.Core.Util;

namespace VikingClip.Core.Audio;

public enum AudioSourceKind { Desktop, Microphone }

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public static class AudioDevices
{
    public static List<AudioDeviceInfo> ListMicrophones()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? def = null;
            try { def = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == def));
        }
        catch (Exception ex) { Log.Warn("Could not list microphones: " + ex.Message); }
        return list;
    }
}

/// <summary>
/// Captures one WASAPI stream (desktop loopback or a microphone), converts it to 48 kHz stereo 16-bit and
/// stamps every chunk with engine time. Follows Windows default-device changes automatically.
/// </summary>
public sealed class AudioSource : IDisposable
{
    private readonly AudioSourceKind _kind;
    private readonly CaptureClock _clock;
    private readonly Func<string?> _deviceId;
    private readonly Func<float> _gain;
    private readonly object _gate = new();

    private MMDeviceEnumerator? _enumerator;
    private Notifier? _notifier;
    private WasapiCapture? _capture;
    private WasapiOut? _keepAlive;
    private WaveFormat? _format;
    private WdlResampler? _resampler;
    private float[] _floatScratch = new float[0];
    private float[] _stereoScratch = new float[0];
    private float[] _resampled = new float[0];
    private System.Threading.Timer? _restartTimer;
    private bool _disposed;

    // timeline anchoring
    private double _anchor;
    private long _framesSinceAnchor;
    private bool _anchored;

    public string? DeviceName { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRunning { get; private set; }
    public AudioSourceKind Kind => _kind;

    public event Action<AudioChunk>? ChunkCaptured;
    public event Action<AudioSource>? StateChanged;

    public AudioSource(AudioSourceKind kind, CaptureClock clock, Func<string?> deviceIdProvider, Func<float> gainProvider)
    {
        _kind = kind;
        _clock = clock;
        _deviceId = deviceIdProvider;
        _gain = gainProvider;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopCaptureLocked();
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                if (_notifier is null)
                {
                    _notifier = new Notifier(this);
                    _enumerator.RegisterEndpointNotificationCallback(_notifier);
                }

                MMDevice device;
                if (_kind == AudioSourceKind.Desktop)
                {
                    device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _capture = new WasapiLoopbackCapture(device);
                    // Loopback only delivers data while something plays; a silent stream keeps it flowing so
                    // the timeline has no holes.
                    _keepAlive = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                    _keepAlive.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
                    _keepAlive.Play();
                }
                else
                {
                    device = ResolveMic(_enumerator);
                    _capture = new WasapiCapture(device, true, 20);
                }

                DeviceName = device.FriendlyName;
                _format = _capture.WaveFormat;
                _resampler = null;
                _anchored = false;
                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += OnStopped;
                _capture.StartRecording();
                IsRunning = true;
                LastError = null;
                Log.Info($"Audio {_kind}: {DeviceName} ({_format.SampleRate} Hz, {_format.Channels} ch, {_format.Encoding})");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                IsRunning = false;
                Log.Error($"Audio {_kind} failed to start", ex);
                ScheduleRestart(TimeSpan.FromSeconds(10));
            }
        }
        StateChanged?.Invoke(this);
    }

    private MMDevice ResolveMic(MMDeviceEnumerator en)
    {
        var id = _deviceId();
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var d = en.GetDevice(id);
                if (d.State == DeviceState.Active) return d;
                Log.Warn($"Selected microphone is not active; using default");
            }
            catch { Log.Warn("Selected microphone not found; using default"); }
        }
        return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    }

    public void Stop()
    {
        lock (_gate) StopCaptureLocked();
        StateChanged?.Invoke(this);
    }

    private void StopCaptureLocked()
    {
        IsRunning = false;
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnStopped;
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
        if (_keepAlive is not null)
        {
            try { _keepAlive.Stop(); } catch { }
            try { _keepAlive.Dispose(); } catch { }
            _keepAlive = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _restartTimer?.Dispose();
            StopCaptureLocked();
            if (_enumerator is not null && _notifier is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_notifier); } catch { }
            }
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    /// <summary>Restart on the thread pool after a short delay (device changes come in bursts).</summary>
    public void ScheduleRestart(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _restartTimer?.Dispose();
            _restartTimer = new System.Threading.Timer(_ => Start(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            LastError = e.Exception.Message;
            Log.Warn($"Audio {_kind} stopped: {e.Exception.Message}; restarting");
            ScheduleRestart(TimeSpan.FromSeconds(2));
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _format;
        if (fmt is null || e.BytesRecorded == 0) return;
        try
        {
            var arrival = _clock.Now;
            var std = fmt is WaveFormatExtensible ext ? ext.ToStandardWaveFormat() : fmt;
            var bytesPerSample = std.BitsPerSample / 8;
            var srcFrames = e.BytesRecorded / (bytesPerSample * std.Channels);
            if (srcFrames <= 0) return;

            // 1. to float, interleaved source channels
            var needed = srcFrames * std.Channels;
            if (_floatScratch.Length < needed) _floatScratch = new float[needed * 2];
            ToFloat(e.Buffer, e.BytesRecorded, std, _floatScratch);

            // 2. to stereo
            if (_stereoScratch.Length < srcFrames * 2) _stereoScratch = new float[srcFrames * 4];
            ToStereo(_floatScratch, srcFrames, std.Channels, _stereoScratch);

            // 3. to 48 kHz
            float[] stereo;
            int frames;
            if (std.SampleRate != AudioChunk.SampleRate)
            {
                _resampler ??= CreateResampler(std.SampleRate);
                var inNeeded = _resampler.ResamplePrepare(srcFrames, 2, out var inBuf, out var inOff);
                Array.Copy(_stereoScratch, 0, inBuf, inOff, Math.Min(inNeeded, srcFrames) * 2);
                var maxOut = (int)Math.Ceiling(srcFrames * (double)AudioChunk.SampleRate / std.SampleRate) + 16;
                if (_resampled.Length < maxOut * 2) _resampled = new float[maxOut * 4];
                frames = _resampler.ResampleOut(_resampled, 0, Math.Min(inNeeded, srcFrames), maxOut, 2);
                stereo = _resampled;
            }
            else
            {
                stereo = _stereoScratch;
                frames = srcFrames;
            }
            if (frames <= 0) return;

            // 4. gain + to 16-bit
            var gain = _gain();
            var samples = new short[frames * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                var v = stereo[i] * gain;
                samples[i] = (short)Math.Clamp((int)Math.Round(v * 32767f), short.MinValue, short.MaxValue);
            }

            // 5. timeline position: sample-counted from an anchor, gently slewed towards wall-clock
            var duration = frames / (double)AudioChunk.SampleRate;
            var wallStart = arrival - duration - (_kind == AudioSourceKind.Desktop ? 0.02 : 0.03);
            double start;
            lock (_gate)
            {
                if (!_anchored)
                {
                    _anchor = wallStart;
                    _framesSinceAnchor = 0;
                    _anchored = true;
                }
                var expected = _anchor + _framesSinceAnchor / (double)AudioChunk.SampleRate;
                var drift = wallStart - expected;
                if (Math.Abs(drift) > 0.5)
                {
                    // Stall or device hiccup: re-anchor (the gap will be filled with silence on export).
                    _anchor = wallStart;
                    _framesSinceAnchor = 0;
                }
                else if (Math.Abs(drift) > 0.02)
                {
                    _anchor += drift * 0.05; // slow correction, inaudible
                }
                start = _anchor + _framesSinceAnchor / (double)AudioChunk.SampleRate;
                _framesSinceAnchor += frames;
            }

            ChunkCaptured?.Invoke(new AudioChunk { Start = start, Samples = samples });
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio {_kind} chunk dropped: {ex.Message}");
        }
    }

    private static WdlResampler CreateResampler(int inRate)
    {
        var r = new WdlResampler();
        r.SetMode(true, 2, false);
        r.SetFilterParms();
        r.SetFeedMode(true); // input driven
        r.SetRates(inRate, AudioChunk.SampleRate);
        return r;
    }

    private static void ToFloat(byte[] buffer, int bytes, WaveFormat fmt, float[] dst)
    {
        var span = buffer.AsSpan(0, bytes);
        switch (fmt.Encoding, fmt.BitsPerSample)
        {
            case (WaveFormatEncoding.IeeeFloat, 32):
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span);
                src.CopyTo(dst);
                break;
            }
            case (WaveFormatEncoding.Pcm, 16):
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(span);
                for (var i = 0; i < src.Length; i++) dst[i] = src[i] / 32768f;
                break;
            }
            case (WaveFormatEncoding.Pcm, 32):
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(span);
                for (var i = 0; i < src.Length; i++) dst[i] = src[i] / 2147483648f;
                break;
            }
            case (WaveFormatEncoding.Pcm, 24):
            {
                var n = bytes / 3;
                for (var i = 0; i < n; i++)
                {
                    var v = (span[i * 3] << 8) | (span[i * 3 + 1] << 16) | (span[i * 3 + 2] << 24);
                    dst[i] = v / 2147483648f;
                }
                break;
            }
            default:
                throw new NotSupportedException($"Audio format {fmt.Encoding} {fmt.BitsPerSample}-bit");
        }
    }

    private static void ToStereo(float[] src, int frames, int channels, float[] dst)
    {
        switch (channels)
        {
            case 1:
                for (var i = 0; i < frames; i++) { dst[i * 2] = src[i]; dst[i * 2 + 1] = src[i]; }
                break;
            case 2:
                Array.Copy(src, 0, dst, 0, frames * 2);
                break;
            default:
                // FL FR FC LFE BL BR (SL SR): fold centre and surrounds in, drop LFE.
                for (var i = 0; i < frames; i++)
                {
                    var b = i * channels;
                    var l = src[b];
                    var r = src[b + 1];
                    if (channels >= 3) { l += 0.707f * src[b + 2]; r += 0.707f * src[b + 2]; }
                    if (channels >= 6) { l += 0.5f * src[b + 4]; r += 0.5f * src[b + 5]; }
                    if (channels >= 8) { l += 0.5f * src[b + 6]; r += 0.5f * src[b + 7]; }
                    dst[i * 2] = l;
                    dst[i * 2 + 1] = r;
                }
                break;
        }
    }

    private sealed class Notifier : IMMNotificationClient
    {
        private readonly AudioSource _owner;
        public Notifier(AudioSource owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (_owner._kind == AudioSourceKind.Desktop && flow == DataFlow.Render && role == Role.Multimedia)
                _owner.ScheduleRestart(TimeSpan.FromMilliseconds(700));
            if (_owner._kind == AudioSourceKind.Microphone && flow == DataFlow.Capture && role == Role.Communications && string.IsNullOrEmpty(_owner._deviceId()))
                _owner.ScheduleRestart(TimeSpan.FromMilliseconds(700));
        }
    }
}

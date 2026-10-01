using VikingClip.Core.Audio;
using VikingClip.Core.Logging;
using VikingClip.Core.Media;
using VikingClip.Core.Settings;
using VikingClip.Core.Util;

namespace VikingClip.Core.Capture;

/// <summary>
/// A manual recording: fragments are appended to a temp file as they arrive (so length is limited by disk,
/// not RAM) together with live audio; Stop() muxes everything into the final MP4.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    private readonly MonitorCapture _capture;
    private readonly AudioSource? _desktop;
    private readonly AudioSource? _mic;
    private readonly AudioSettings _audio;
    private readonly CaptureClock _clock;
    private readonly string _tmp;
    private readonly object _gate = new();

    private readonly List<Part> _parts = new();
    private Part? _current;
    private FileStream? _desktopPcm, _micPcm;
    private PcmTimelineWriter? _desktopWriter, _micWriter;
    private readonly List<AudioChunk> _pendingDesktop = new(), _pendingMic = new();
    private double? _audioStart;
    private bool _stopped;

    private sealed class Part
    {
        public required string Path;
        public required FileStream Stream;
        public required InitSegment Init;
        public double Start;
        public double End;
        public int Fragments;
    }

    public string OutputPath { get; }
    public string GameLabel { get; }
    public double RequestedStart { get; }
    public bool IsRecording => !_stopped;
    public double Elapsed => Math.Max(0, _clock.Now - (_audioStart ?? RequestedStart));
    public MonitorCapture Capture => _capture;

    public RecordingSession(MonitorCapture capture, AudioSource? desktop, AudioSource? mic, AudioSettings audio, CaptureClock clock, string outputPath, string gameLabel)
    {
        _capture = capture;
        _desktop = desktop;
        _mic = mic;
        _audio = audio;
        _clock = clock;
        OutputPath = outputPath;
        GameLabel = gameLabel;
        RequestedStart = clock.Now;
        _tmp = Paths.NewTempDir("rec");

        capture.Ring.FragmentAdded += OnFragment;
        if (desktop is not null) desktop.ChunkCaptured += OnDesktopChunk;
        if (mic is not null) mic.ChunkCaptured += OnMicChunk;
        Log.Info($"Recording started on {capture.Monitor.DeviceName} -> {outputPath}");
    }

    private void OnFragment(Fragment f)
    {
        lock (_gate)
        {
            if (_stopped) return;
            // Start cleanly at the first keyframe after the user pressed Record.
            if (_current is null && _parts.Count == 0 && f.Start < RequestedStart) return;

            if (_current is null || !ReferenceEquals(_current.Init, f.Init))
            {
                if (_current is not null) _current.Stream.Flush();
                var path = Path.Combine(_tmp, $"part{_parts.Count + 1}.mp4");
                var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                fs.Write(f.Init.Bytes);
                _current = new Part { Path = path, Stream = fs, Init = f.Init, Start = f.Start };
                _parts.Add(_current);
            }

            _current.Stream.Write(f.Bytes);
            _current.End = f.End;
            _current.Fragments++;

            if (_audioStart is null)
            {
                _audioStart = f.Start;
                var offset = _audio.OffsetMs / 1000.0;
                if (_desktop is not null)
                {
                    _desktopPcm = new FileStream(Path.Combine(_tmp, "desktop.pcm"), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                    _desktopWriter = new PcmTimelineWriter(_desktopPcm, f.Start - offset);
                    foreach (var c in _pendingDesktop) _desktopWriter.Append(c);
                    _pendingDesktop.Clear();
                }
                if (_mic is not null)
                {
                    _micPcm = new FileStream(Path.Combine(_tmp, "mic.pcm"), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                    _micWriter = new PcmTimelineWriter(_micPcm, f.Start - offset);
                    foreach (var c in _pendingMic) _micWriter.Append(c);
                    _pendingMic.Clear();
                }
            }
        }
    }

    private void OnDesktopChunk(AudioChunk c)
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_desktopWriter is null) { _pendingDesktop.Add(c); TrimPending(_pendingDesktop); }
            else _desktopWriter.Append(c);
        }
    }

    private void OnMicChunk(AudioChunk c)
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_micWriter is null) { _pendingMic.Add(c); TrimPending(_pendingMic); }
            else _micWriter.Append(c);
        }
    }

    private static void TrimPending(List<AudioChunk> list)
    {
        while (list.Count > 0 && list[^1].End - list[0].Start > 10) list.RemoveAt(0);
    }

    public async Task<ClipResult> StopAsync(CancellationToken ct = default)
    {
        double end;
        lock (_gate)
        {
            if (_stopped) return ClipResult.Fail("Already stopped");
            _stopped = true;
            end = _clock.Now;
        }
        _capture.Ring.FragmentAdded -= OnFragment;
        if (_desktop is not null) _desktop.ChunkCaptured -= OnDesktopChunk;
        if (_mic is not null) _mic.ChunkCaptured -= OnMicChunk;

        try
        {
            lock (_gate)
            {
                foreach (var p in _parts) p.Stream.Flush();
                var offset = _audio.OffsetMs / 1000.0;
                _desktopWriter?.FinishAt(end - offset);
                _micWriter?.FinishAt(end - offset);
                _desktopPcm?.Dispose();
                _micPcm?.Dispose();
                foreach (var p in _parts) p.Stream.Dispose();
            }

            if (_parts.Count == 0 || _audioStart is null)
                return ClipResult.Fail("Nothing was recorded (no frames arrived). Is the capture running?");

            var recStart = _audioStart.Value;
            var totalDuration = end - recStart;
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);

            if (_parts.Count == 1)
            {
                var r = await Mux(_parts[0], recStart, end, OutputPath, ct).ConfigureAwait(false);
                if (!r.Success) return ClipResult.Fail("Could not write the recording: " + r.Summary);
            }
            else
            {
                // ffmpeg restarted mid-recording: mux each part, then stitch.
                var partFiles = new List<string>();
                for (var i = 0; i < _parts.Count; i++)
                {
                    var p = _parts[i];
                    var partOut = Path.Combine(_tmp, $"muxed{i + 1}.mp4");
                    var partEnd = i == _parts.Count - 1 ? end : p.End;
                    var r = await Mux(p, recStart, partEnd, partOut, ct).ConfigureAwait(false);
                    if (!r.Success) return ClipResult.Fail("Could not write the recording: " + r.Summary);
                    partFiles.Add(partOut);
                }
                var list = Path.Combine(_tmp, "concat.txt");
                await File.WriteAllLinesAsync(list, partFiles.Select(f => $"file '{f.Replace("'", "'\\''")}'"), ct).ConfigureAwait(false);
                var c = await Ffmpeg.RunAsync(FfmpegArgs.Concat(list, OutputPath), TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
                if (!c.Success)
                {
                    // Different resolution across parts: keep the parts as separate files rather than fail.
                    for (var i = 0; i < partFiles.Count; i++)
                    {
                        var dst = Path.Combine(Path.GetDirectoryName(OutputPath)!, Path.GetFileNameWithoutExtension(OutputPath) + $" part {i + 1}.mp4");
                        File.Move(partFiles[i], dst, true);
                    }
                    Log.Warn("Recording parts could not be concatenated; saved as separate files");
                    return new ClipResult(true, OutputPath, totalDuration, 0, null, Truncated: true);
                }
            }

            var size = new FileInfo(OutputPath).Length;
            Log.Info($"Recording saved {OutputPath} ({FileNames.HumanDuration(totalDuration)}, {FileNames.HumanSize(size)})");
            return new ClipResult(true, OutputPath, totalDuration, size, null);
        }
        catch (Exception ex)
        {
            Log.Error("Recording stop failed", ex);
            return ClipResult.Fail(ex.Message);
        }
        finally
        {
            try { Directory.Delete(_tmp, true); } catch { }
        }
    }

    private Task<FfmpegResult> Mux(Part p, double recStart, double partEnd, string output, CancellationToken ct)
    {
        var skip = Math.Max(0, p.Start - recStart);
        var duration = partEnd - p.Start;
        var args = FfmpegArgs.Mux(p.Path,
            _desktopPcm is not null ? Path.Combine(_tmp, "desktop.pcm") : null,
            _micPcm is not null ? Path.Combine(_tmp, "mic.pcm") : null,
            skip, duration, _audio.DesktopGain, _audio.MicrophoneGain, output);
        return Ffmpeg.RunAsync(args, TimeSpan.FromMinutes(10), ct);
    }

    public void Dispose()
    {
        if (!_stopped) _ = StopAsync();
    }
}

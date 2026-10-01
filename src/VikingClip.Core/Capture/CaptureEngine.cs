using System.Drawing;
using VikingClip.Core.Audio;
using VikingClip.Core.Display;
using VikingClip.Core.Logging;
using VikingClip.Core.Media;
using VikingClip.Core.Settings;
using VikingClip.Core.Util;

namespace VikingClip.Core.Capture;

public enum EngineState { Stopped, Starting, Running, Degraded, Failed }

/// <summary>
/// Runs the whole always-on capture: one <see cref="MonitorCapture"/> per monitor plus desktop/mic audio,
/// and exposes the three actions (clip, record, screenshot) on top of them.
/// </summary>
public sealed class CaptureEngine : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<MonitorCapture> _captures = new();
    private readonly AudioRing _desktopRing;
    private readonly AudioRing _micRing;
    private AudioSource? _desktop;
    private AudioSource? _mic;
    private Timer? _watchdog;
    private Timer? _displayChangeTimer;
    private List<MonitorInfo> _monitors = new();
    private bool _gpuScaler;
    private string _statusText = "Stopped";

    public CaptureClock Clock { get; } = new();
    public EngineState State { get; private set; } = EngineState.Stopped;
    public string StatusText => _statusText;
    public List<string> Warnings { get; } = new();
    public string FfmpegVersion { get; private set; } = "";

    public IReadOnlyList<MonitorCapture> Captures { get { lock (_captures) return _captures.ToList(); } }
    public IReadOnlyList<MonitorInfo> Monitors => _monitors;
    public AudioSource? DesktopSource => _desktop;
    public AudioSource? MicSource => _mic;
    public AudioRing? DesktopRing => _desktop is null ? null : _desktopRing;
    public AudioRing? MicRing => _mic is null ? null : _micRing;

    /// <summary>Raised (on a thread-pool thread) whenever state/health changes.</summary>
    public event Action? Changed;

    public CaptureEngine(SettingsStore settings)
    {
        _settings = settings;
        _desktopRing = new AudioRing(RingSeconds);
        _micRing = new AudioRing(RingSeconds);
    }

    /// <summary>Video/audio kept in memory: the clip length plus a margin for keyframe alignment and latency.</summary>
    public double RingSeconds => _settings.Current.ClipLengthSeconds + 6;

    public long EstimatedRamBytes => Captures.Sum(c => c.Ring.Bytes) + _desktopRing.Bytes + _micRing.Bytes;

    // ---- lifecycle --------------------------------------------------------------------------

    public async Task StartAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetState(EngineState.Starting, "Starting…");
            Warnings.Clear();

            if (!Ffmpeg.IsAvailable)
            {
                SetState(EngineState.Failed, "ffmpeg.exe is missing. Reinstall VikingClip (or run tools\\get-ffmpeg.ps1 in a dev checkout).");
                return;
            }
            FfmpegVersion = Ffmpeg.VersionLine();

            var s = _settings.Current;
            var all = DisplayEnumerator.Enumerate();
            _monitors = s.Capture.Monitors == MonitorMode.PrimaryOnly
                ? all.Where(m => m.IsPrimary).DefaultIfEmpty(all.FirstOrDefault()!).Where(m => m is not null).ToList()
                : all;
            if (_monitors.Count == 0)
            {
                SetState(EngineState.Failed, "No monitors found (DXGI enumeration returned nothing).");
                return;
            }

            // scale_d3d11 exists in the bundled build but cannot configure on ddagrab frames (probed),
            // so GPU-frame plans record at native resolution; a resolution cap uses the CPU-copy variant.
            _gpuScaler = false;
            var fingerprint = DisplayEnumerator.Fingerprint(_monitors, Ffmpeg.ExePath);
            progress?.Report("Checking GPU encoders…");
            var probe = await EncoderProbe.ProbeAsync(_monitors, s.Capture.EncoderOverride, s.EncoderCache, fingerprint, _gpuScaler, progress, ct).ConfigureAwait(false);
            if (!probe.FromCache && probe.Failures.Count == 0 && s.Capture.EncoderOverride is null)
            {
                _settings.Update(x => x.EncoderCache = new EncoderCache
                {
                    Fingerprint = fingerprint,
                    PlanByAdapter = probe.PlanByAdapter.ToDictionary(k => k.Key, v => v.Value.Id),
                });
            }
            Warnings.AddRange(probe.Failures);

            lock (_captures)
            {
                foreach (var m in _monitors)
                {
                    if (!probe.PlanByAdapter.TryGetValue(m.AdapterIndex, out var plan)) continue;
                    var monitor = m;
                    var p = ParamsFor(monitor);
                    if (plan.GpuFrames && !_gpuScaler && p.MaxHeight > 0 && monitor.Height > p.MaxHeight)
                    {
                        plan = plan.ScalableVariant();
                        Log.Info($"{monitor.DeviceName}: resolution cap {p.MaxHeight}p requested; using {plan.DisplayName}");
                    }
                    var cap = new MonitorCapture(monitor, plan, Clock, RingSeconds, () => ParamsFor(monitor), _gpuScaler);
                    cap.StateChanged += _ => RaiseChanged();
                    _captures.Add(cap);
                    cap.Start();
                }
            }

            _desktopRing.MaxSeconds = RingSeconds;
            _micRing.MaxSeconds = RingSeconds;
            _desktopRing.Clear();
            _micRing.Clear();
            if (s.Audio.CaptureDesktop)
            {
                _desktop = new AudioSource(AudioSourceKind.Desktop, Clock, () => null, () => 1f);
                _desktop.ChunkCaptured += _desktopRing.Add;
                _desktop.StateChanged += _ => RaiseChanged();
                _desktop.Start();
            }
            if (s.Audio.CaptureMicrophone)
            {
                _mic = new AudioSource(AudioSourceKind.Microphone, Clock, () => _settings.Current.Audio.MicrophoneDeviceId, () => 1f);
                _mic.ChunkCaptured += _micRing.Add;
                _mic.StateChanged += _ => RaiseChanged();
                _mic.Start();
            }

            _watchdog = new Timer(_ => Watchdog(), null, 1000, 1000);
            if (Captures.Count == 0)
                SetState(EngineState.Failed, Warnings.FirstOrDefault() ?? "No capture could be started.");
            else
                SetState(EngineState.Running, "Running");
        }
        catch (Exception ex)
        {
            Log.Error("Engine start failed", ex);
            SetState(EngineState.Failed, "Start failed: " + ex.Message);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetState(EngineState.Stopped, "Stopped");
        }
        finally { _lifecycle.Release(); }
    }

    private Task StopCoreAsync()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        List<MonitorCapture> caps;
        lock (_captures)
        {
            caps = _captures.ToList();
            _captures.Clear();
        }
        return Task.Run(() =>
        {
            Parallel.ForEach(caps, c => c.Dispose());
            _desktop?.Dispose();
            _mic?.Dispose();
            _desktop = null;
            _mic = null;
        });
    }

    public Task RestartAsync(IProgress<string>? progress = null) => StartAsync(progress);

    /// <summary>Settings that require the pipeline to be rebuilt (anything else applies live).</summary>
    public static bool RequiresRestart(AppSettings a, AppSettings b)
    {
        var ca = a.Capture; var cb = b.Capture;
        return ca.Quality != cb.Quality || ca.MaxHeight != cb.MaxHeight || ca.Fps != cb.Fps || ca.BitrateKbps != cb.BitrateKbps ||
               ca.EncoderOverride != cb.EncoderOverride || ca.Monitors != cb.Monitors || ca.DrawCursor != cb.DrawCursor ||
               a.Audio.CaptureDesktop != b.Audio.CaptureDesktop || a.Audio.CaptureMicrophone != b.Audio.CaptureMicrophone ||
               a.Audio.MicrophoneDeviceId != b.Audio.MicrophoneDeviceId;
    }

    /// <summary>Clip length changed: resize the buffers without restarting.</summary>
    public void ApplyClipLength()
    {
        foreach (var c in Captures) c.Ring.MaxSeconds = RingSeconds;
        _desktopRing.MaxSeconds = RingSeconds;
        _micRing.MaxSeconds = RingSeconds;
    }

    /// <summary>Call on WM_DISPLAYCHANGE; restarts after things settle.</summary>
    public void NotifyDisplayChange()
    {
        _displayChangeTimer?.Dispose();
        _displayChangeTimer = new Timer(_ =>
        {
            Log.Info("Display configuration changed; restarting capture");
            _ = RestartAsync();
        }, null, 2500, Timeout.Infinite);
    }

    public CaptureParams ParamsFor(MonitorInfo m)
    {
        var c = _settings.Current.Capture;
        // Auto = native resolution (GPU path, no scaling cost). Custom may cap the height.
        var maxHeight = c.Quality == QualityMode.Auto ? 0 : c.MaxHeight;
        var (w, h) = EncoderPlan.OutputSize(m.Width, m.Height, maxHeight);
        var kbps = c.Quality == QualityMode.Custom && c.BitrateKbps > 0 ? c.BitrateKbps : FfmpegArgs.AutoBitrateKbps(w, h, c.Fps);
        return new CaptureParams(c.Fps, kbps, maxHeight, c.DrawCursor);
    }

    // ---- lookups ----------------------------------------------------------------------------

    public MonitorCapture? CaptureFor(IntPtr hMonitor)
    {
        var caps = Captures;
        if (caps.Count == 0) return null;
        var m = DisplayEnumerator.FindByHMonitor(_monitors, hMonitor);
        return (m is null ? null : caps.FirstOrDefault(c => c.Monitor.DeviceName == m.DeviceName))
               ?? caps.FirstOrDefault(c => c.Monitor.IsPrimary) ?? caps[0];
    }

    // ---- actions ----------------------------------------------------------------------------

    public Task<ClipResult> SaveClipAsync(MonitorCapture capture, double endTime, double lengthSeconds, string outputPath, CancellationToken ct = default) =>
        ClipWriter.WriteAsync(capture, DesktopRing, MicRing, _settings.Current.Audio, endTime, lengthSeconds, outputPath, ct);

    public RecordingSession StartRecording(MonitorCapture capture, string outputPath, string gameLabel) =>
        new(capture, _desktop, _mic, _settings.Current.Audio, Clock, outputPath, gameLabel);

    public Bitmap Screenshot(MonitorInfo monitor) => ScreenshotGrabber.Grab(monitor);

    // ---- health -----------------------------------------------------------------------------

    private void Watchdog()
    {
        var caps = Captures;
        foreach (var c in caps) c.CheckWatchdog();
        if (State is EngineState.Stopped or EngineState.Starting or EngineState.Failed) return;
        var healthy = caps.Count(c => c.IsHealthy);
        var next = healthy == caps.Count ? EngineState.Running : healthy == 0 ? EngineState.Degraded : EngineState.Degraded;
        var text = next == EngineState.Running
            ? "Running"
            : string.Join("; ", caps.Where(c => !c.IsHealthy).Select(c => $"{c.Monitor.DeviceName}: {c.State}{(c.LastError is null ? "" : " - " + c.LastError)}"));
        if (next != State || text != _statusText) SetState(next, text);
    }

    private void SetState(EngineState s, string text)
    {
        State = s;
        _statusText = text;
        Log.Info($"Engine: {s} - {text}");
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Warn("Engine change handler threw: " + ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        _displayChangeTimer?.Dispose();
        await StopAsync().ConfigureAwait(false);
    }
}

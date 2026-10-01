using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using VikingClip.Core.Display;
using VikingClip.Core.Logging;
using VikingClip.Core.Media;
using VikingClip.Core.Native;
using VikingClip.Core.Util;

namespace VikingClip.Core.Capture;

public enum CaptureState { Stopped, Starting, Running, Restarting, Failed }

/// <summary>
/// Owns one ffmpeg process capturing one monitor and feeds its output into a <see cref="FragmentRing"/>.
/// Restarts ffmpeg with backoff when it dies (display mode change, lock screen, driver reset...).
/// </summary>
public sealed partial class MonitorCapture : IDisposable
{
    private readonly CaptureClock _clock;
    private readonly Func<CaptureParams> _params;
    private readonly bool _gpuScaler;
    private Thread? _thread;
    private Process? _proc;
    private volatile bool _stopping;
    private int _sessionCounter;
    private double _lastFragmentAt = -1;
    private double _sessionStartedAt;
    private int _fragmentsThisSession;
    private long _bytesWindow;
    private double _bytesWindowStart;

    public MonitorInfo Monitor { get; }
    public EncoderPlan Plan { get; }
    public FragmentRing Ring { get; }
    public CaptureState State { get; private set; } = CaptureState.Stopped;
    public string? LastError { get; private set; }
    public int Restarts { get; private set; }
    public CaptureParams? ActiveParams { get; private set; }
    public double MeasuredKbps { get; private set; }
    public bool HasExactTiming { get; private set; }

    public event Action<MonitorCapture>? StateChanged;

    public MonitorCapture(MonitorInfo monitor, EncoderPlan plan, CaptureClock clock, double ringSeconds, Func<CaptureParams> paramsProvider, bool gpuScalerAvailable)
    {
        Monitor = monitor;
        Plan = plan;
        _clock = clock;
        _params = paramsProvider;
        _gpuScaler = gpuScalerAvailable;
        Ring = new FragmentRing(ringSeconds);
    }

    public void Start()
    {
        if (_thread is not null) return;
        _stopping = false;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = $"capture {Monitor.DeviceName}" };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        KillProcess(graceful: true);
        _thread?.Join(3000);
        _thread = null;
        SetState(CaptureState.Stopped);
    }

    public void Dispose() => Stop();

    /// <summary>True when fragments arrived recently.</summary>
    public bool IsHealthy => State == CaptureState.Running && _lastFragmentAt >= 0 && _clock.Now - _lastFragmentAt < 4;

    private void RunLoop()
    {
        var backoff = TimeSpan.FromSeconds(1);
        var consecutiveFailures = 0;
        while (!_stopping)
        {
            var started = _clock.Now;
            try
            {
                SetState(Restarts == 0 ? CaptureState.Starting : CaptureState.Restarting);
                RunOnce();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error($"Capture {Monitor.DeviceName} crashed", ex);
            }
            if (_stopping) break;

            var ranFor = _clock.Now - started;
            consecutiveFailures = ranFor > 30 ? 0 : consecutiveFailures + 1;
            if (consecutiveFailures >= 8)
            {
                SetState(CaptureState.Failed);
                LastError ??= "ffmpeg keeps exiting immediately";
                Log.Error($"Capture {Monitor.DeviceName} gave up after {consecutiveFailures} failures: {LastError}");
                // Keep trying slowly - the display might come back (e.g. after the lock screen).
                Thread.Sleep(TimeSpan.FromSeconds(20));
                consecutiveFailures = 4;
                continue;
            }
            Restarts++;
            backoff = ranFor > 30 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Min(15, backoff.TotalSeconds * 2));
            Log.Info($"Capture {Monitor.DeviceName} restarting in {backoff.TotalSeconds:0}s (ran {ranFor:0.0}s, {LastError})");
            Thread.Sleep(backoff);
        }
    }

    private void RunOnce()
    {
        var p = _params();
        ActiveParams = p;
        var sessionEpoch = CaptureClock.UnixMicrosNow();
        var parser = new FragmentParser(++_sessionCounter, _clock.FromUnixMicros(sessionEpoch));
        InitSegment? init = null;
        double? reportedFirstPts = null;
        _fragmentsThisSession = 0;
        _lastFragmentAt = -1;
        _sessionStartedAt = _clock.Now;
        HasExactTiming = false;

        parser.InitReceived += i =>
        {
            init = i;
            if (reportedFirstPts is { } v) i.FirstFramePts = v;
        };
        parser.FragmentReceived += f =>
        {
            var now = _clock.Now;
            f.ArrivalTime = now;
            // Until ffmpeg's exact report arrives, estimate: the fragment was finished ~one frame after its last pts.
            if (f.Init.FirstFramePts is null && f.Init.EstimatedFirstFramePts is null)
                f.Init.EstimatedFirstFramePts = (now - f.Init.SessionOffset) - (f.RelativeStart + f.Duration) - 0.03;
            HasExactTiming = f.Init.HasExactTiming;
            _lastFragmentAt = now;
            _fragmentsThisSession++;
            UpdateBitrate(f.Bytes.Length, now);
            Ring.Add(f);
            if (State != CaptureState.Running) SetState(CaptureState.Running);
        };

        var args = FfmpegArgs.Capture(Monitor, Plan, p, sessionEpoch, _gpuScaler);
        var psi = Ffmpeg.Psi(args, redirectStdout: true);
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderrTail = new Queue<string>();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = e.Data;
            var m = FirstPtsLine().Match(line);
            if (m.Success && reportedFirstPts is null && line.Contains("Parsed_metadata", StringComparison.Ordinal))
            {
                reportedFirstPts = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (init is not null) init.FirstFramePts = reportedFirstPts;
                Log.Debug($"{Monitor.DeviceName}: first frame pts {reportedFirstPts:0.000}s");
                return;
            }
            lock (stderrTail)
            {
                stderrTail.Enqueue(line);
                while (stderrTail.Count > 40) stderrTail.Dequeue();
            }
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                Log.Warn($"ffmpeg[{Monitor.DeviceName}]: {line}");
        };

        Log.Info($"Starting capture {Monitor.DeviceName} ({Monitor.Width}x{Monitor.Height}) with {Plan.DisplayName}, {p.Fps} fps, {p.BitrateKbps} kbps");
        Log.Debug("ffmpeg " + string.Join(' ', args.Select(a => a.Contains(' ') || a.Contains(';') ? $"\"{a}\"" : a)));
        proc.Start();
        ChildProcessJob.Add(proc);
        proc.BeginErrorReadLine();
        _proc = proc;

        var stdout = proc.StandardOutput.BaseStream;
        var buffer = new byte[256 * 1024];
        var startedAt = _clock.Now;
        try
        {
            int n;
            while (!_stopping && (n = stdout.Read(buffer, 0, buffer.Length)) > 0)
            {
                parser.Feed(buffer.AsSpan(0, n));
            }
        }
        catch (IOException) when (_stopping) { }
        finally
        {
            if (!proc.HasExited) KillProcess(graceful: !_stopping);
            proc.WaitForExit(2000);
            string tail;
            lock (stderrTail) tail = string.Join(" | ", stderrTail.TakeLast(3));
            if (!_stopping)
            {
                LastError = proc.HasExited && proc.ExitCode != 0
                    ? $"ffmpeg exited with code {proc.ExitCode}: {tail}"
                    : $"ffmpeg stopped: {tail}";
                if (_fragmentsThisSession == 0 && _clock.Now - startedAt < 5)
                    Log.Warn($"Capture {Monitor.DeviceName} produced nothing: {tail}");
            }
            _proc = null;
            proc.Dispose();
        }
    }

    /// <summary>Called by the engine's watchdog; kills a stalled ffmpeg so the loop restarts it.</summary>
    public void CheckWatchdog()
    {
        if (_stopping || _proc is null) return;
        var now = _clock.Now;
        var stalled = _fragmentsThisSession > 0
            ? now - _lastFragmentAt > 6
            : now - _sessionStartedAt > 25;
        if (stalled)
        {
            LastError = "no frames for several seconds (display changed or driver reset?)";
            Log.Warn($"Capture {Monitor.DeviceName} stalled; restarting ffmpeg");
            KillProcess(graceful: false);
        }
    }

    private void KillProcess(bool graceful)
    {
        var p = _proc;
        if (p is null) return;
        try
        {
            if (p.HasExited) return;
            if (graceful)
            {
                try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
                if (p.WaitForExit(1500)) return;
            }
            p.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private void UpdateBitrate(int bytes, double now)
    {
        if (_bytesWindowStart == 0 || now - _bytesWindowStart > 5)
        {
            if (_bytesWindowStart != 0) MeasuredKbps = _bytesWindow * 8.0 / 1000.0 / (now - _bytesWindowStart);
            _bytesWindowStart = now;
            _bytesWindow = 0;
        }
        _bytesWindow += bytes;
    }

    private void SetState(CaptureState s)
    {
        if (State == s) return;
        State = s;
        try { StateChanged?.Invoke(this); } catch { }
    }

    [GeneratedRegex(@"pts_time:\s*([0-9.]+)")]
    private static partial Regex FirstPtsLine();
}

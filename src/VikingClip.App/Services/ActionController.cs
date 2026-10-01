using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using VikingClip.App.Windows;
using VikingClip.Core;
using VikingClip.Core.Capture;
using VikingClip.Core.Discord;
using VikingClip.Core.Display;
using VikingClip.Core.Games;
using VikingClip.Core.Library;
using VikingClip.Core.Logging;
using VikingClip.Core.Native;
using VikingClip.Core.Settings;
using VikingClip.Core.Util;

namespace VikingClip.App.Services;

public enum PanelAction { Clip, Record, StopRecording, Screenshot }

/// <summary>Where something goes: this PC, or a Discord channel (Discord only - nothing is kept locally unless the post fails).</summary>
public sealed record Destination(string Id, DiscordChannel? Channel)
{
    public static readonly Destination Local = new("local", null);
    public bool IsLocal => Channel is null;
    public string Label => Channel?.Name ?? "This PC";
}

/// <summary>Everything we know about the instant the hotkey was pressed - frozen before any UI appears.</summary>
public sealed class Moment
{
    public required double Time { get; init; }
    public required DateTime When { get; init; }
    public required ForegroundInfo Foreground { get; init; }
    public required GameInfo Game { get; init; }
    public MonitorInfo? Monitor { get; init; }
    public MonitorCapture? Capture { get; init; }
    public Task<Bitmap?> Screenshot { get; init; } = Task.FromResult<Bitmap?>(null);
}

/// <summary>A recording that has been stopped and is being written while the user picks a destination.</summary>
public sealed class PendingRecording
{
    public required RecordingSession Session { get; init; }
    public required Task<ClipResult> Mux { get; init; }
    public required GameInfo Game { get; init; }
    public MonitorInfo? Monitor { get; init; }
}

/// <summary>Turns hotkeys and panel choices into clips, recordings, screenshots and Discord posts.</summary>
public sealed class ActionController
{
    private readonly App _app;
    private PanelWindow? _panel;
    private ToastWindow? _toast;
    private RecordingIndicatorWindow? _indicator;
    private RecordingSession? _recording;
    private GameInfo? _recordingGame;
    private MonitorInfo? _recordingMonitor;
    private RECT _lastMonitorRect;

    public bool IsRecording => _recording?.IsRecording == true;
    public RecordingSession? Recording => _recording;

    public event Action? RecordingChanged;
    public event Action? LibraryChanged;

    public ActionController(App app) => _app = app;

    private SettingsStore Settings => _app.Settings;
    private CaptureEngine Engine => _app.Engine;

    // ---- hotkeys --------------------------------------------------------------------------------

    public void OnHotkey(string name)
    {
        switch (name)
        {
            case "panel": TogglePanel(); break;
            case "clip": _ = InstantClipAsync(); break;
            case "record": _ = ToggleRecordingAsync(); break;
            case "screenshot": _ = InstantScreenshotAsync(); break;
        }
    }

    public Moment Snapshot(bool grabScreenshot)
    {
        var time = Engine.Clock.Now;
        var fg = ForegroundInfo.Capture();
        var monitor = DisplayEnumerator.FindByHMonitor(Engine.Monitors, fg.HMonitor)
                      ?? Engine.Monitors.FirstOrDefault(m => m.IsPrimary) ?? Engine.Monitors.FirstOrDefault();
        var capture = Engine.CaptureFor(fg.HMonitor);
        var game = _app.Games.Detect(fg);
        if (monitor is not null) _lastMonitorRect = monitor.Bounds;
        Log.Info($"Hotkey: fg='{fg.Title}' exe={fg.ExeName} game={game.Name}({game.Source}) monitor={monitor?.DeviceName} exclusive={fg.IsExclusiveFullscreen} covers={fg.CoversMonitor}");

        Task<Bitmap?> shot = Task.FromResult<Bitmap?>(null);
        if (grabScreenshot && monitor is not null)
        {
            var m = monitor;
            shot = Task.Run(() =>
            {
                try { return (Bitmap?)Engine.Screenshot(m); }
                catch (Exception ex) { Log.Error("Screenshot grab failed", ex); return null; }
            });
        }

        return new Moment { Time = time, When = DateTime.Now, Foreground = fg, Game = game, Monitor = monitor, Capture = capture, Screenshot = shot };
    }

    private PanelWindow Panel => _panel ??= new PanelWindow(this);

    public void TogglePanel()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.CloseAndRestore();
            return;
        }
        Panel.ShowFor(Snapshot(grabScreenshot: true));
    }

    public void ClosePanel() => _panel?.CloseAndRestore();

    // ---- destinations -------------------------------------------------------------------------

    public List<Destination> Destinations()
    {
        var list = new List<Destination> { Destination.Local };
        list.AddRange(Settings.Current.DiscordChannels
            .Where(c => !string.IsNullOrEmpty(c.WebhookProtected))
            .Select(c => new Destination(c.Id.ToString(), c)));
        return list;
    }

    public Destination DefaultDestination()
    {
        var s = Settings.Current;
        if (s.LastDestination is { } id)
        {
            var hit = Destinations().FirstOrDefault(d => d.Id == id);
            if (hit is not null) return hit;
        }
        return Destination.Local;
    }

    public void RememberDestination(Destination d, bool remember)
    {
        Settings.Update(s =>
        {
            s.LastDestination = d.Id;
            s.RememberDestination = remember;
        });
    }

    // ---- actions ------------------------------------------------------------------------------

    public Task ExecuteAsync(Moment m, PanelAction action, Destination dest) => action switch
    {
        PanelAction.Clip => SaveClipAsync(m, dest),
        PanelAction.Screenshot => SaveScreenshotAsync(m, dest),
        PanelAction.Record => StartRecordingAsync(m),
        PanelAction.StopRecording => BeginStopRecording() is { } p ? FinishRecordingAsync(p, dest) : Task.CompletedTask,
        _ => Task.CompletedTask,
    };

    public Task InstantClipAsync() => SaveClipAsync(Snapshot(false), DefaultDestination());

    public Task InstantScreenshotAsync() => SaveScreenshotAsync(Snapshot(true), DefaultDestination());

    /// <summary>Record hotkey: start, or stop and ask where the recording should go.</summary>
    public async Task ToggleRecordingAsync()
    {
        if (!IsRecording)
        {
            await StartRecordingAsync(Snapshot(false));
            return;
        }
        var moment = Snapshot(false);
        var pending = BeginStopRecording();
        if (pending is null) return;
        Panel.AskDestination(moment, PanelAction.StopRecording,
            onPick: dest => _ = FinishRecordingAsync(pending, dest),
            onDismiss: () => _ = FinishRecordingAsync(pending, Destination.Local));
    }

    public async Task SaveClipAsync(Moment m, Destination dest)
    {
        var s = Settings.Current;
        if (m.Capture is null)
        {
            Toast.Show(ToastKind.Error, "Capture isn't running", Engine.StatusText);
            return;
        }

        var finalPath = FileNames.BuildOutputPath(s.ClipsRoot, m.Game.FolderName, m.Game.Label, ".mp4", m.When);
        // Discord-only: build the clip in a temp folder so nothing is left on this PC after a successful post.
        var workPath = dest.IsLocal ? finalPath : Path.Combine(Paths.NewTempDir("post"), Path.GetFileName(finalPath));

        Toast.Progress("Saving clip…", m.Game.Name);
        var r = await Engine.SaveClipAsync(m.Capture, m.Time, s.ClipLengthSeconds, workPath);
        if (!r.Success)
        {
            Toast.Show(ToastKind.Error, "Clip failed", r.Error);
            return;
        }
        var meta = new ClipMeta
        {
            Kind = MediaKind.Clip,
            Game = m.Game.FolderName,
            DurationSeconds = r.DurationSeconds,
            Monitor = m.Monitor?.DeviceName,
            Encoder = m.Capture.Plan.Id,
            CreatedLocal = m.When,
        };
        var summary = $"{m.Game.Name} · {FileNames.HumanDuration(r.DurationSeconds)} · {FileNames.HumanSize(r.Bytes)}{(r.Truncated ? " · shortened (capture restarted)" : "")}";

        if (dest.IsLocal)
        {
            RecordLocal(finalPath, meta);
            Toast.Show(ToastKind.Success, "Clip saved", summary, onClick: () => App.RevealInExplorer(finalPath));
            return;
        }

        await DeliverToDiscordAsync(workPath, finalPath, dest.Channel!, meta, m.Game.Name, m.Monitor, summary);
    }

    public async Task SaveScreenshotAsync(Moment m, Destination dest)
    {
        var s = Settings.Current;
        Bitmap? bmp = null;
        try
        {
            bmp = await m.Screenshot;
            if (bmp is null && m.Monitor is not null)
                bmp = await Task.Run(() => Engine.Screenshot(m.Monitor));
        }
        catch (Exception ex) { Log.Error("Screenshot failed", ex); }
        if (bmp is null)
        {
            Toast.Show(ToastKind.Error, "Screenshot failed", "Could not read the screen.");
            return;
        }

        var finalPath = FileNames.BuildOutputPath(s.ClipsRoot, m.Game.FolderName, m.Game.Label, ".png", m.When);
        var workPath = dest.IsLocal ? finalPath : Path.Combine(Paths.NewTempDir("post"), Path.GetFileName(finalPath));
        try
        {
            await Task.Run(() => ScreenshotGrabber.SavePng(bmp, workPath));
        }
        finally { bmp.Dispose(); }

        var meta = new ClipMeta { Kind = MediaKind.Screenshot, Game = m.Game.FolderName, Monitor = m.Monitor?.DeviceName, CreatedLocal = m.When };
        var summary = $"{m.Game.Name} · {FileNames.HumanSize(new FileInfo(workPath).Length)}";

        if (dest.IsLocal)
        {
            RecordLocal(finalPath, meta);
            Toast.Show(ToastKind.Success, "Screenshot saved", summary, onClick: () => App.RevealInExplorer(finalPath));
            return;
        }

        await DeliverToDiscordAsync(workPath, finalPath, dest.Channel!, meta, m.Game.Name, m.Monitor, summary);
    }

    public async Task StartRecordingAsync(Moment m)
    {
        if (IsRecording) return;
        if (m.Capture is null)
        {
            Toast.Show(ToastKind.Error, "Capture isn't running", Engine.StatusText);
            return;
        }
        var s = Settings.Current;
        var path = FileNames.BuildOutputPath(s.ClipsRoot, m.Game.FolderName, $"{m.Game.Label} Recording", ".mp4", m.When);
        _recording = Engine.StartRecording(m.Capture, path, m.Game.Label);
        _recording.LowDiskSpace += (rec, reason) => _app.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(rec, _recording)) return;
            Toast.Show(ToastKind.Error, "Recording stopped", reason + " - saving what was recorded.", duration: TimeSpan.FromSeconds(8));
            if (BeginStopRecording() is { } p) _ = FinishRecordingAsync(p, Destination.Local);
        });
        _recordingGame = m.Game;
        _recordingMonitor = m.Monitor;
        RecordingChanged?.Invoke();
        if (s.Capture.ShowRecordingIndicator && m.Monitor is not null)
        {
            _indicator ??= new RecordingIndicatorWindow();
            _indicator.ShowFor(_recording, m.Monitor.Bounds);
        }
        Toast.Show(ToastKind.Info, "Recording started", $"{m.Game.Name} · {s.Hotkeys.ToggleRecording} or {s.Hotkeys.Panel} to stop");
        await Task.CompletedTask;
    }

    /// <summary>Stops the recording at this instant and starts writing the file; the destination can be chosen meanwhile.</summary>
    public PendingRecording? BeginStopRecording()
    {
        var rec = _recording;
        if (rec is null) return null;
        _recording = null;
        RecordingChanged?.Invoke();
        _indicator?.HideIndicator();
        Toast.Progress("Saving recording…", _recordingGame?.Name);
        return new PendingRecording
        {
            Session = rec,
            Mux = rec.StopAsync(),
            Game = _recordingGame ?? GameInfo.Desktop(null),
            Monitor = _recordingMonitor,
        };
    }

    public async Task FinishRecordingAsync(PendingRecording p, Destination dest)
    {
        Log.Info($"Recording destination: {dest.Label}");
        var r = await p.Mux;
        if (!r.Success)
        {
            Toast.Show(ToastKind.Error, "Recording failed", r.Error);
            return;
        }
        var path = p.Session.OutputPath;
        var meta = new ClipMeta
        {
            Kind = MediaKind.Recording,
            Game = p.Game.FolderName,
            DurationSeconds = r.DurationSeconds,
            Monitor = p.Monitor?.DeviceName,
            Encoder = p.Session.Capture.Plan.Id,
            CreatedLocal = DateTime.Now,
        };
        var summary = $"{p.Game.Name} · {FileNames.HumanDuration(r.DurationSeconds)} · {FileNames.HumanSize(r.Bytes)}";

        if (dest.IsLocal)
        {
            RecordLocal(path, meta);
            Toast.Show(ToastKind.Success, "Recording saved", summary, onClick: () => App.RevealInExplorer(path));
            return;
        }

        // Recordings are always muxed into the clips folder (disk-bound); Discord-only removes the file after a good post.
        await DeliverToDiscordAsync(path, path, dest.Channel!, meta, p.Game.Name, p.Monitor, summary);
    }

    // ---- Discord ------------------------------------------------------------------------------

    /// <summary>
    /// Discord-only delivery: post <paramref name="workPath"/>; on success delete it (nothing stays on this PC),
    /// on failure keep it at <paramref name="keepPath"/> so nothing is lost.
    /// </summary>
    private async Task DeliverToDiscordAsync(string workPath, string keepPath, DiscordChannel ch, ClipMeta meta, string gameName, MonitorInfo? monitor, string summary)
    {
        var ok = await PostToDiscordAsync(workPath, ch, meta, gameName, monitor);
        if (ok)
        {
            Toast.Show(ToastKind.Success, $"Posted to {ch.Name}", summary + " · not kept on this PC");
            TryDelete(workPath);
            return;
        }
        try
        {
            if (!string.Equals(workPath, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(keepPath)!);
                File.Move(workPath, keepPath, true);
                TryDelete(Path.GetDirectoryName(workPath)!);
            }
            RecordLocal(keepPath, meta);
            Toast.Show(ToastKind.Error, $"Couldn't post to {ch.Name}", "Saved on this PC instead - send it from the Library when Discord is back.", onClick: () => App.RevealInExplorer(keepPath), duration: TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            Log.Error("Fallback save failed", ex);
        }
    }

    /// <summary>Posts a file to a channel, compressing a copy first if it exceeds the channel's limit. Keeps the source file.</summary>
    public async Task<bool> PostToDiscordAsync(string path, DiscordChannel ch, ClipMeta meta, string gameName, MonitorInfo? monitor)
    {
        var limit = (long)ch.MaxUploadMB * 1024 * 1024;
        var upload = path;
        string? temp = null;
        try
        {
            var size = new FileInfo(path).Length;
            if (DiscordCompressor.NeedsCompression(size, limit))
            {
                if (meta.Kind == MediaKind.Screenshot)
                {
                    temp = Path.Combine(Paths.TempDir, $"discord-{Guid.NewGuid():N}.jpg");
                    await Task.Run(() => SaveJpeg(path, temp, 85));
                    upload = temp;
                }
                else
                {
                    var duration = meta.DurationSeconds > 0 ? meta.DurationSeconds : (await Core.Media.Ffmpeg.ProbeDurationAsync(path) ?? 45);
                    var (w, h) = monitor is not null
                        ? EncoderPlan.OutputSize(monitor.Width, monitor.Height, Settings.Current.Capture.Quality == QualityMode.Custom ? Settings.Current.Capture.MaxHeight : 0)
                        : (1920, 1080);
                    var plan = DiscordCompressor.MakePlan(duration, limit, w, h, Settings.Current.Capture.Fps);
                    temp = Path.Combine(Paths.TempDir, $"discord-{Guid.NewGuid():N}.mp4");
                    var progress = new Progress<string>(msg => Toast.Progress(msg, $"{ch.Name} · {plan.Width}×{plan.Height} @ {plan.Fps} fps"));
                    var (ok, err) = await DiscordCompressor.CompressAsync(path, temp, plan, duration, progress);
                    if (!ok)
                    {
                        Log.Warn($"Compression for {ch.Name} failed: {err}");
                        Toast.Show(ToastKind.Error, "Couldn't compress for Discord", err);
                        return false;
                    }
                    upload = temp;
                }
            }

            var message = (ch.MessageTemplate ?? "")
                .Replace("{game}", gameName)
                .Replace("{user}", Environment.UserName)
                .Replace("{kind}", meta.Kind.ToString())
                .Replace("{length}", FileNames.HumanDuration(meta.DurationSeconds));

            Toast.Progress($"Uploading to {ch.Name}…", FileNames.HumanSize(new FileInfo(upload).Length), 0);
            var uploadProgress = new Progress<double>(f => Toast.Progress($"Uploading to {ch.Name}…", $"{f * 100:0}%", f));
            var res = await DiscordWebhook.UploadAsync(ch.GetWebhookUrl(), upload, message, ch.PosterName, uploadProgress);
            if (res.Success)
            {
                Log.Info($"Posted {Path.GetFileName(upload)} ({FileNames.HumanSize(new FileInfo(upload).Length)}) to {ch.Name}: {res.AttachmentUrl}");
                return true;
            }
            Log.Warn($"Discord post to {ch.Name} failed: {res.Error}");
            Toast.Show(ToastKind.Error, "Discord post failed", res.Error, duration: TimeSpan.FromSeconds(8));
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Discord post failed", ex);
            Toast.Show(ToastKind.Error, "Discord post failed", ex.Message);
            return false;
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
        }
    }

    /// <summary>Library page: send an existing file to a channel (the file stays).</summary>
    public async Task SendExistingToDiscordAsync(string path, DiscordChannel ch, ClipMeta meta, string gameName)
    {
        if (await PostToDiscordAsync(path, ch, meta, gameName, null))
            Toast.Show(ToastKind.Success, $"Posted to {ch.Name}", Path.GetFileName(path));
    }

    private void RecordLocal(string path, ClipMeta meta)
    {
        _app.Library.Record(path, meta);
        LibraryChanged?.Invoke();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                var dir = Path.GetDirectoryName(path);
                if (dir is not null && dir.StartsWith(Paths.TempDir, StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            else if (Directory.Exists(path) && path.StartsWith(Paths.TempDir, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) { Log.Debug($"Cleanup skipped for {path}: {ex.Message}"); }
    }

    private static void SaveJpeg(string pngPath, string jpgPath, long quality)
    {
        using var img = Image.FromFile(pngPath);
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        img.Save(jpgPath, codec, p);
    }

    // ---- toasts -------------------------------------------------------------------------------

    public ToastWindow Toast
    {
        get
        {
            _toast ??= new ToastWindow(() => _lastMonitorRect, () => Settings.Current.ShowToasts);
            return _toast;
        }
    }

    public async Task ShutdownAsync()
    {
        _panel?.Hide();
        if (BeginStopRecording() is { } p) await FinishRecordingAsync(p, Destination.Local);
        _indicator?.Close();
        _toast?.Close();
    }
}

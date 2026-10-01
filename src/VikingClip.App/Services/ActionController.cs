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

/// <summary>Where a clip goes: this PC only, or this PC + a Discord channel.</summary>
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

    public void TogglePanel()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.CloseAndRestore();
            return;
        }
        var moment = Snapshot(grabScreenshot: true);
        _panel ??= new PanelWindow(this);
        _panel.ShowFor(moment);
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
        PanelAction.StopRecording => StopRecordingAsync(dest),
        _ => Task.CompletedTask,
    };

    public Task InstantClipAsync() => SaveClipAsync(Snapshot(false), DefaultDestination());

    public Task InstantScreenshotAsync() => SaveScreenshotAsync(Snapshot(true), DefaultDestination());

    public Task ToggleRecordingAsync() => IsRecording ? StopRecordingAsync(DefaultDestination()) : StartRecordingAsync(Snapshot(false));

    public async Task SaveClipAsync(Moment m, Destination dest)
    {
        var s = Settings.Current;
        if (m.Capture is null)
        {
            Toast.Show(ToastKind.Error, "Capture isn't running", Engine.StatusText);
            return;
        }
        var path = FileNames.BuildOutputPath(s.ClipsRoot, m.Game.FolderName, m.Game.Label, ".mp4", m.When);
        Toast.Progress("Saving clip…", m.Game.Name);
        var r = await Engine.SaveClipAsync(m.Capture, m.Time, s.ClipLengthSeconds, path);
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
        _app.Library.Record(path, meta);
        LibraryChanged?.Invoke();
        Toast.Show(ToastKind.Success, "Clip saved",
            $"{m.Game.Name} · {FileNames.HumanDuration(r.DurationSeconds)} · {FileNames.HumanSize(r.Bytes)}{(r.Truncated ? " · shortened (capture restarted)" : "")}",
            onClick: () => App.RevealInExplorer(path));

        if (dest.Channel is { } ch)
            await PostToDiscordAsync(path, ch, meta, m.Game.Name, m.Monitor);
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
        var path = FileNames.BuildOutputPath(s.ClipsRoot, m.Game.FolderName, m.Game.Label, ".png", m.When);
        try
        {
            await Task.Run(() => ScreenshotGrabber.SavePng(bmp, path));
        }
        finally { bmp.Dispose(); }

        var meta = new ClipMeta { Kind = MediaKind.Screenshot, Game = m.Game.FolderName, Monitor = m.Monitor?.DeviceName, CreatedLocal = m.When };
        _app.Library.Record(path, meta);
        LibraryChanged?.Invoke();
        Toast.Show(ToastKind.Success, "Screenshot saved", $"{m.Game.Name} · {FileNames.HumanSize(new FileInfo(path).Length)}", onClick: () => App.RevealInExplorer(path));

        if (dest.Channel is { } ch)
            await PostToDiscordAsync(path, ch, meta, m.Game.Name, m.Monitor);
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
        _recording.LowDiskSpace += (rec, reason) => _app.Dispatcher.BeginInvoke(async () =>
        {
            if (!ReferenceEquals(rec, _recording)) return;
            Toast.Show(ToastKind.Error, "Recording stopped", reason + " - saving what was recorded.", duration: TimeSpan.FromSeconds(8));
            await StopRecordingAsync(null);
        });
        _recordingGame = m.Game;
        _recordingMonitor = m.Monitor;
        RecordingChanged?.Invoke();
        if (s.Capture.ShowRecordingIndicator && m.Monitor is not null)
        {
            _indicator ??= new RecordingIndicatorWindow();
            _indicator.ShowFor(_recording, m.Monitor.Bounds);
        }
        Toast.Show(ToastKind.Info, "Recording started", $"{m.Game.Name} · press {s.Hotkeys.ToggleRecording} or Alt+K to stop");
        await Task.CompletedTask;
    }

    public async Task StopRecordingAsync(Destination? dest)
    {
        var rec = _recording;
        if (rec is null) return;
        _recording = null;
        RecordingChanged?.Invoke();
        _indicator?.HideIndicator();
        Toast.Progress("Saving recording…", _recordingGame?.Name);
        var r = await rec.StopAsync();
        if (!r.Success)
        {
            Toast.Show(ToastKind.Error, "Recording failed", r.Error);
            return;
        }
        var meta = new ClipMeta
        {
            Kind = MediaKind.Recording,
            Game = _recordingGame?.FolderName ?? Paths.DesktopFolderName,
            DurationSeconds = r.DurationSeconds,
            Monitor = _recordingMonitor?.DeviceName,
            Encoder = rec.Capture.Plan.Id,
            CreatedLocal = DateTime.Now,
        };
        _app.Library.Record(rec.OutputPath, meta);
        LibraryChanged?.Invoke();
        Toast.Show(ToastKind.Success, "Recording saved",
            $"{_recordingGame?.Name} · {FileNames.HumanDuration(r.DurationSeconds)} · {FileNames.HumanSize(r.Bytes)}",
            onClick: () => App.RevealInExplorer(rec.OutputPath));

        if (dest?.Channel is { } ch)
            await PostToDiscordAsync(rec.OutputPath, ch, meta, _recordingGame?.Name ?? "Desktop", _recordingMonitor);
    }

    // ---- Discord ------------------------------------------------------------------------------

    /// <summary>Posts a saved file to a channel, compressing a copy first if it exceeds the channel's limit.</summary>
    public async Task PostToDiscordAsync(string path, DiscordChannel ch, ClipMeta meta, string gameName, MonitorInfo? monitor)
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
                        Toast.Show(ToastKind.Error, "Couldn't compress for Discord", err);
                        return;
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
                Toast.Show(ToastKind.Success, $"Posted to {ch.Name}", gameName);
            }
            else
            {
                Log.Warn($"Discord post to {ch.Name} failed: {res.Error}");
                Toast.Show(ToastKind.Error, $"Discord post failed", res.Error, duration: TimeSpan.FromSeconds(8));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Discord post failed", ex);
            Toast.Show(ToastKind.Error, "Discord post failed", ex.Message);
        }
        finally
        {
            if (temp is not null) { try { File.Delete(temp); } catch { } }
        }
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
        if (IsRecording) await StopRecordingAsync(null);
        _indicator?.Close();
        _toast?.Close();
    }
}

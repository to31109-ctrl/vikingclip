using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VikingClip.App.Windows;
using VikingClip.Core.Games;
using VikingClip.Core.Logging;
using VikingClip.Core.Native;
using VikingClip.Core.Settings;

namespace VikingClip.App.Services;

/// <summary>
/// Dev helper: `VikingClip.exe --ui-snapshots &lt;dir&gt;` renders every page, the Alt+K panel and a toast to PNG
/// files off-screen and exits. Used for design review and README screenshots without touching the desktop.
/// </summary>
public static class UiSnapshots
{
    public static async Task RunAsync(App app, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);

            // Demo channels so the destination step has something to show (in-memory only, never saved).
            var s = app.Settings.Current;
            if (s.DiscordChannels.Count == 0)
            {
                s.DiscordChannels.Add(new DiscordChannel { Name = "#clips", WebhookProtected = "demo", MaxUploadMB = 10 });
                s.DiscordChannels.Add(new DiscordChannel { Name = "#private", WebhookProtected = "demo", MaxUploadMB = 50 });
            }

            // Off-screen and fully transparent: nothing of this must ever be visible on the desktop.
            var main = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                Opacity = 0,
            };
            main.Show();
            await Task.Delay(500);
            foreach (var page in new[] { "library", "discord", "capture", "audio", "hotkeys", "storage", "about" })
            {
                main.NavigateTo(page);
                await Task.Delay(page == "library" ? 2500 : 500);
                Render(main, Path.Combine(dir, $"page-{page}.png"));
            }
            main.Close();

            var fg = ForegroundInfo.Capture();
            var moment = new Moment
            {
                Time = app.Engine.Clock.Now,
                When = DateTime.Now,
                Foreground = fg,
                Game = new GameInfo("VALORANT", true, "demo", null),
                Monitor = app.Engine.Monitors.FirstOrDefault() ?? Core.Display.DisplayEnumerator.Enumerate().FirstOrDefault(),
                Capture = null,
            };
            var panel = new PanelWindow(app.Actions) { SnapshotMode = true, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            panel.ShowFor(moment);
            await Task.Delay(300);
            Render(panel, Path.Combine(dir, "panel-actions.png"));
            panel.ChooseForSnapshot(PanelAction.Clip);
            await Task.Delay(300);
            Render(panel, Path.Combine(dir, "panel-destinations.png"));
            panel.Close();

            var toast = new ToastWindow(() => new RECT { Right = 1920, Bottom = 1080 }, () => true) { SnapshotMode = true, Left = -20000, Top = -20000, ShowInTaskbar = false, Opacity = 0 };
            toast.Show(ToastKind.Success, "Clip saved", "VALORANT · 0:45 · 112 MB");
            await Task.Delay(400);
            Render(toast, Path.Combine(dir, "toast-success.png"));
            toast.Progress("Uploading to #clips…", "63%", 0.63);
            await Task.Delay(300);
            Render(toast, Path.Combine(dir, "toast-progress.png"));
            toast.Close();

            Log.Info($"UI snapshots written to {dir}");
        }
        catch (Exception ex)
        {
            Log.Error("UI snapshots failed", ex);
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static void Render(Window w, string path)
    {
        w.UpdateLayout();
        // Render the client content at its own size (Window.ActualWidth includes the OS frame).
        if (w.Content is not FrameworkElement content || content.ActualWidth <= 0 || content.ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(w);
        var cw = content.ActualWidth;
        var chh = content.ActualHeight;
        var width = (int)Math.Ceiling(cw * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(chh * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background ?? Brushes.Transparent, null, new Rect(0, 0, cw, chh));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, cw, chh));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}

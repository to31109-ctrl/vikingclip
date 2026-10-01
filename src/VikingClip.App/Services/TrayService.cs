using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using VikingClip.Core.Capture;
using VikingClip.Core.Logging;

namespace VikingClip.App.Services;

/// <summary>The notification-area icon: the app's home while it runs in the background.</summary>
public sealed class TrayService : IDisposable
{
    private readonly App _app;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;

    public TrayService(App app)
    {
        _app = app;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open VikingClip", () => _app.ShowMainWindow()));
        menu.Items.Add(Item($"Clip last {_app.Settings.Current.ClipLengthSeconds} s", () => _ = _app.Actions.InstantClipAsync()));
        menu.Items.Add(Item("Open clips folder", () => _app.OpenClipsFolder()));
        menu.Items.Add(new Separator());
        _pauseItem = Item("Pause capture", () => _ = TogglePauseAsync());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", () => _ = _app.ExitAsync()));

        _icon = new TaskbarIcon
        {
            ToolTipText = "VikingClip",
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/icon.ico");
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is not null) _icon.Icon = new System.Drawing.Icon(stream);
        }
        catch (Exception ex) { Log.Warn("Tray icon image missing: " + ex.Message); }

        _icon.TrayLeftMouseUp += (_, _) => _app.ShowMainWindow();
        _icon.ForceCreate(enablesEfficiencyMode: false);

        _app.Engine.Changed += () => _app.Dispatcher.BeginInvoke(RefreshTooltip);
        RefreshTooltip();
    }

    private static MenuItem Item(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    private async Task TogglePauseAsync()
    {
        if (_app.Engine.State == EngineState.Stopped)
        {
            _pauseItem.Header = "Pause capture";
            await _app.Engine.StartAsync();
        }
        else
        {
            _pauseItem.Header = "Resume capture";
            await _app.Engine.StopAsync();
        }
    }

    public void RefreshTooltip()
    {
        var e = _app.Engine;
        var text = e.State switch
        {
            EngineState.Running => $"VikingClip · capturing {e.Captures.Count} monitor{(e.Captures.Count == 1 ? "" : "s")}",
            EngineState.Starting => "VikingClip · starting…",
            EngineState.Degraded => "VikingClip · capture problem (open for details)",
            EngineState.Failed => "VikingClip · capture failed (open for details)",
            _ => "VikingClip · paused",
        };
        _icon.ToolTipText = text;
        _pauseItem.Header = e.State == EngineState.Stopped ? "Resume capture" : "Pause capture";
    }

    public void ShowBalloon(string title, string message)
    {
        try { _icon.ShowNotification(title, message); } catch { }
    }

    public void Dispose()
    {
        try { _icon.Dispose(); } catch { }
    }
}

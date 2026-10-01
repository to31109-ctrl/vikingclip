using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VikingClip.App.Services;
using VikingClip.Core.Capture;

namespace VikingClip.App.Windows;

public partial class MainWindow : Window
{
    private readonly App _app = App.Current;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowNative.ApplyDarkTitleBar(this);
        VersionText.Text = "v" + _app.Updates.CurrentVersion;
        _app.Engine.Changed += OnEngineChanged;
        _app.Updates.Changed += OnUpdateChanged;
        OnEngineChanged();
        OnUpdateChanged();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Closing the window keeps the app in the tray; Quit is in the tray menu.
        _app.Engine.Changed -= OnEngineChanged;
        _app.Updates.Changed -= OnUpdateChanged;
    }

    private void OnEngineChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var e = _app.Engine;
            var (brush, text) = e.State switch
            {
                EngineState.Running => ("AccentBrush", $"Capturing {e.Captures.Count} monitor{(e.Captures.Count == 1 ? "" : "s")}"),
                EngineState.Starting => ("WarningBrush", "Starting capture…"),
                EngineState.Degraded => ("WarningBrush", "Capture problem"),
                EngineState.Failed => ("DangerBrush", "Capture failed"),
                _ => ("TextFaintBrush", "Capture paused"),
            };
            StatusDot.Fill = (Brush)FindResource(brush);
            StatusText.Text = text;
            StatusText.ToolTip = e.StatusText;
        });
    }

    private void OnUpdateChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var u = _app.Updates;
            if (u.Downloaded is not null)
            {
                UpdateText.Text = "Update ready · restart";
                UpdateText.Visibility = Visibility.Visible;
            }
            else UpdateText.Visibility = Visibility.Collapsed;
        });
    }

    private void UpdateText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => _app.Updates.ApplyAndRestart();

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (LibraryPage is null) return; // during InitializeComponent
        foreach (var (nav, page) in new (RadioButton, UIElement)[]
                 {
                     (NavLibrary, LibraryPage), (NavDiscord, DiscordPage), (NavCapture, CapturePage), (NavAudio, AudioPage),
                     (NavHotkeys, HotkeysPage), (NavStorage, StoragePage), (NavAbout, AboutPage),
                 })
        {
            var visible = ReferenceEquals(nav, sender);
            page.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible && page is Pages.IPage p) p.OnShown();
        }
    }

    public void NavigateTo(string page)
    {
        var target = page switch
        {
            "discord" => NavDiscord,
            "capture" => NavCapture,
            "audio" => NavAudio,
            "hotkeys" => NavHotkeys,
            "storage" => NavStorage,
            "about" => NavAbout,
            _ => NavLibrary,
        };
        target.IsChecked = true;
    }
}

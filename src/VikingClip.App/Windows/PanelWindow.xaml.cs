using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VikingClip.App.Services;
using VikingClip.Core.Logging;
using VikingClip.Core.Native;
using VikingClip.Core.Util;

namespace VikingClip.App.Windows;

/// <summary>
/// The Alt+K panel. Takes focus on purpose: in a borderless game the game simply pauses input while the
/// panel is up; a true exclusive-fullscreen game alt-tabs out (as agreed) and gets focus back when we close.
/// Keyboard-first so it works even when a game has the mouse locked.
/// </summary>
public partial class PanelWindow : Window
{
    private readonly ActionController _controller;
    private Moment? _moment;
    private IntPtr _restoreTo;
    private PanelAction? _pendingAction;
    private List<Destination> _destinations = new();

    public PanelWindow(ActionController controller)
    {
        _controller = controller;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowNative.ExcludeFromCapture(this);
            WindowNative.MakeOverlay(this, noActivate: false);
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) =>
        {
            // Clicking back into the game closes the panel - but don't fight the game for focus.
            // Ignore the activation churn right after showing (exclusive-fullscreen games minimize, etc.).
            if (IsVisible && (DateTime.UtcNow - _shownAt).TotalMilliseconds > 1200)
                Dispatcher.BeginInvoke(() => { if (IsVisible && !IsActive) Hide(); }, DispatcherPriority.Background);
        };
    }

    private DateTime _shownAt;

    public void ShowFor(Moment moment)
    {
        _moment = moment;
        _pendingAction = null;
        _restoreTo = moment.Foreground.Hwnd;
        _shownAt = DateTime.UtcNow;

        var s = App.Current.Settings.Current;
        ClipLabel.Text = $"Clip last {s.ClipLengthSeconds} s";
        ContextText.Text = string.Join(" · ", new[] { moment.Game.IsGame ? moment.Game.Name : "Desktop", moment.Monitor?.Label }.Where(x => !string.IsNullOrEmpty(x)));
        RefreshRecordingState();
        StatusText.Text = moment.Capture is null
            ? "Capture is not running on this monitor."
            : moment.Capture.State != Core.Capture.CaptureState.Running
                ? $"Capture: {moment.Capture.State}"
                : "";
        ActionsStep.Visibility = Visibility.Visible;
        DestinationStep.Visibility = Visibility.Collapsed;

        Show();
        if (moment.Monitor is not null)
        {
            WindowNative.CenterOnMonitor(this, moment.Monitor.Bounds);
            // DPI change on the way to another monitor can resize us; settle once more.
            Dispatcher.BeginInvoke(() => WindowNative.CenterOnMonitor(this, moment.Monitor.Bounds), DispatcherPriority.Loaded);
        }
        var hwnd = WindowNative.Handle(this);
        if (!User32.ForceForeground(hwnd)) Log.Warn("Panel could not take foreground");
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private void RefreshRecordingState()
    {
        var rec = _controller.Recording;
        if (rec is { IsRecording: true })
        {
            RecordLabel.Text = $"Stop · {FileNames.HumanDuration(rec.Elapsed)}";
            RecordGlyph.Text = "";
        }
        else
        {
            RecordLabel.Text = "Record";
            RecordGlyph.Text = "";
        }
    }

    public void CloseAndRestore()
    {
        if (!IsVisible) return;
        Hide();
        var target = _restoreTo;
        _restoreTo = IntPtr.Zero;
        if (target != IntPtr.Zero && User32.IsWindow(target))
        {
            // Give the game back its focus (and un-minimize it if the alt-tab minimized it).
            Dispatcher.BeginInvoke(() => User32.ForceForeground(target), DispatcherPriority.Background);
        }
    }

    // ---- step 1 -------------------------------------------------------------------------------

    private void Clip_Click(object sender, RoutedEventArgs e) => Choose(PanelAction.Clip);
    private void Screenshot_Click(object sender, RoutedEventArgs e) => Choose(PanelAction.Screenshot);

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.IsRecording) Choose(PanelAction.StopRecording);
        else Run(PanelAction.Record, Destination.Local);
    }

    private void Choose(PanelAction action)
    {
        if (_moment is null) return;
        _destinations = _controller.Destinations();
        var s = App.Current.Settings.Current;
        if (_destinations.Count == 1 || (s.RememberDestination && s.LastDestination is not null))
        {
            Run(action, _controller.DefaultDestination());
            return;
        }
        _pendingAction = action;
        DestTitle.Text = action switch
        {
            PanelAction.Clip => "Save clip to",
            PanelAction.Screenshot => "Save screenshot to",
            PanelAction.StopRecording => "Save recording to",
            _ => "Save to",
        };
        RememberBox.IsChecked = false;
        DestList.Items.Clear();
        for (var i = 0; i < _destinations.Count; i++)
            DestList.Items.Add(MakeDestinationRow(_destinations[i], i + 1));
        ActionsStep.Visibility = Visibility.Collapsed;
        DestinationStep.Visibility = Visibility.Visible;
    }

    private Button MakeDestinationRow(Destination d, int index)
    {
        var glyph = new TextBlock { Text = d.IsLocal ? "" : "", Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 0, 12, 0) };
        if (!d.IsLocal) glyph.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var title = new TextBlock { Text = d.Label, Style = (Style)FindResource("Body") };
        var sub = new TextBlock
        {
            Text = d.IsLocal ? $"Clips\\{_moment?.Game.FolderName}" : $"Discord · also saved on this PC · {d.Channel!.MaxUploadMB} MB limit",
            Style = (Style)FindResource("Caption"),
        };
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(sub);
        var hint = new Border { Style = (Style)FindResource("KeyHint"), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = index.ToString(), Style = (Style)FindResource("KeyHintText") } };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(hint, 2);
        grid.Children.Add(glyph);
        grid.Children.Add(text);
        grid.Children.Add(hint);
        var btn = new Button { Style = (Style)FindResource("DestRow"), Content = grid, Tag = d };
        btn.Click += (_, _) => PickDestination(d);
        return btn;
    }

    private void PickDestination(Destination d)
    {
        if (_pendingAction is not { } action) return;
        if (RememberBox.IsChecked == true || d.Id != App.Current.Settings.Current.LastDestination)
            _controller.RememberDestination(d, RememberBox.IsChecked == true);
        Run(action, d);
    }

    private void Run(PanelAction action, Destination dest)
    {
        var m = _moment;
        CloseAndRestore();
        if (m is null) return;
        _ = _controller.ExecuteAsync(m, action, dest);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _pendingAction = null;
        DestinationStep.Visibility = Visibility.Collapsed;
        ActionsStep.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseAndRestore();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _restoreTo = IntPtr.Zero;
        App.Current.ShowMainWindow();
    }

    // ---- keyboard -----------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Escape)
        {
            if (DestinationStep.Visibility == Visibility.Visible) Back_Click(this, new RoutedEventArgs());
            else CloseAndRestore();
            return;
        }

        var digit = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
            _ => 0,
        };

        if (DestinationStep.Visibility == Visibility.Visible)
        {
            if (digit >= 1 && digit <= _destinations.Count) PickDestination(_destinations[digit - 1]);
            else if (key == Key.Enter && _destinations.Count > 0) PickDestination(_destinations[0]);
            else if (key == Key.Back) Back_Click(this, new RoutedEventArgs());
            return;
        }

        switch (key)
        {
            case Key.C: case Key.Enter: Clip_Click(this, new RoutedEventArgs()); break;
            case Key.R: Record_Click(this, new RoutedEventArgs()); break;
            case Key.S: Screenshot_Click(this, new RoutedEventArgs()); break;
            default:
                if (digit == 1) Clip_Click(this, new RoutedEventArgs());
                else if (digit == 2) Record_Click(this, new RoutedEventArgs());
                else if (digit == 3) Screenshot_Click(this, new RoutedEventArgs());
                else e.Handled = false;
                break;
        }
    }
}

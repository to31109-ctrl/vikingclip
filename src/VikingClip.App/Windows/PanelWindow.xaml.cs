using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private List<Destination> _destinations = new();
    private Action<Destination>? _onPick;
    private Action? _onDismiss;
    private DateTime _shownAt;

    /// <summary>Dev snapshots: skip positioning, focus stealing, capture exclusion and motion.</summary>
    public bool SnapshotMode { get; set; }

    public PanelWindow(ActionController controller)
    {
        _controller = controller;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            if (SnapshotMode) return;
            WindowNative.ExcludeFromCapture(this);
            WindowNative.MakeOverlay(this, noActivate: false);
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) =>
        {
            // Clicking back into the game closes the panel - but don't fight the game for focus.
            // Ignore the activation churn right after showing (exclusive-fullscreen games minimize, etc.).
            if (IsVisible && (DateTime.UtcNow - _shownAt).TotalMilliseconds > 1200)
                Dispatcher.BeginInvoke(() => { if (IsVisible && !IsActive) CloseAndRestore(restoreFocus: false); }, DispatcherPriority.Background);
        };
    }

    // ---- showing ------------------------------------------------------------------------------

    /// <summary>Step 1: Clip · Record · Screenshot.</summary>
    public void ShowFor(Moment moment)
    {
        Populate(moment);
        ActionsStep.Visibility = Visibility.Visible;
        DestinationStep.Visibility = Visibility.Collapsed;
        Present();
    }

    /// <summary>Straight to "save to…" for something that already happened (a recording stopped by hotkey).</summary>
    public void AskDestination(Moment moment, PanelAction action, Action<Destination> onPick, Action? onDismiss)
    {
        Populate(moment);
        if (!ShowDestinations(action, onPick, onDismiss)) return; // decided without asking
        Present();
    }

    private void Populate(Moment moment)
    {
        _moment = moment;
        _onPick = null;
        _onDismiss = null;
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
    }

    private void Present()
    {
        if (!IsVisible)
        {
            Opacity = SnapshotMode ? 1 : 0;
            Show();
        }
        if (SnapshotMode) return;
        if (_moment?.Monitor is { } mon)
        {
            WindowNative.CenterOnMonitor(this, mon.Bounds);
            // DPI change on the way to another monitor can resize us; settle once more.
            Dispatcher.BeginInvoke(() => WindowNative.CenterOnMonitor(this, mon.Bounds), DispatcherPriority.Loaded);
        }
        var hwnd = WindowNative.Handle(this);
        if (!User32.ForceForeground(hwnd)) Log.Warn("Panel could not take foreground");
        Activate();
        Focus();
        Keyboard.Focus(this);
        Enter();
    }

    /// <summary>The one authored motion moment: a short fade + settle, exponential ease-out.
    /// (Transforms go on the content: WPF does not allow a RenderTransform on the Window itself.)</summary>
    private void Enter()
    {
        var scale = new ScaleTransform(0.97, 0.97);
        Root.RenderTransform = scale;
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var d = TimeSpan.FromMilliseconds(160);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, d) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, d) { EasingFunction = ease });
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

    public void CloseAndRestore(bool restoreFocus = true)
    {
        if (!IsVisible) return;
        Hide();
        var dismiss = _onDismiss;
        _onDismiss = null;
        _onPick = null;
        var target = _restoreTo;
        _restoreTo = IntPtr.Zero;
        if (restoreFocus && target != IntPtr.Zero && User32.IsWindow(target))
        {
            // Give the game back its focus (and un-minimize it if the alt-tab minimized it).
            Dispatcher.BeginInvoke(() => User32.ForceForeground(target), DispatcherPriority.Background);
        }
        dismiss?.Invoke();
    }

    // ---- step 1 -------------------------------------------------------------------------------

    private void Clip_Click(object sender, RoutedEventArgs e) => Choose(PanelAction.Clip);
    private void Screenshot_Click(object sender, RoutedEventArgs e) => Choose(PanelAction.Screenshot);

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.IsRecording)
        {
            // Stop at this instant; the file is written while the destination is chosen.
            var pending = _controller.BeginStopRecording();
            if (pending is null) { CloseAndRestore(); return; }
            RefreshRecordingState();
            ShowDestinations(PanelAction.StopRecording,
                onPick: dest => _ = _controller.FinishRecordingAsync(pending, dest),
                onDismiss: () => _ = _controller.FinishRecordingAsync(pending, Destination.Local));
        }
        else
        {
            Run(PanelAction.Record, Destination.Local);
        }
    }

    internal void ChooseForSnapshot(PanelAction action) => ShowDestinations(action, _ => { }, null, force: true);

    private void Choose(PanelAction action)
    {
        if (_moment is null) return;
        ShowDestinations(action, onPick: dest => Run(action, dest), onDismiss: null);
    }

    /// <summary>
    /// Shows the "save to" list, or decides immediately when there is only one option / the choice is remembered.
    /// Returns true when the list is being shown.
    /// </summary>
    private bool ShowDestinations(PanelAction action, Action<Destination> onPick, Action? onDismiss, bool force = false)
    {
        _destinations = _controller.Destinations();
        var s = App.Current.Settings.Current;
        if (!force && (_destinations.Count == 1 || (s.RememberDestination && s.LastDestination is not null)))
        {
            var d = _controller.DefaultDestination();
            CloseAndRestore();
            onPick(d);
            return false;
        }
        _onPick = onPick;
        _onDismiss = onDismiss;
        Log.Debug($"Panel: asking destination for {action} ({_destinations.Count} options)");
        DestTitle.Text = action switch
        {
            PanelAction.Clip => "Save clip to",
            PanelAction.Screenshot => "Save screenshot to",
            PanelAction.StopRecording => "Save recording to",
            _ => "Save to",
        };
        BackButton.Visibility = action == PanelAction.StopRecording ? Visibility.Collapsed : Visibility.Visible;
        RememberBox.IsChecked = false;
        DestList.Items.Clear();
        for (var i = 0; i < _destinations.Count; i++)
            DestList.Items.Add(MakeDestinationRow(_destinations[i], i + 1));
        ActionsStep.Visibility = Visibility.Collapsed;
        DestinationStep.Visibility = Visibility.Visible;
        return true;
    }

    private Button MakeDestinationRow(Destination d, int index)
    {
        var glyph = new TextBlock { Text = d.IsLocal ? "" : "", Style = (Style)FindResource("Icon"), Margin = new Thickness(0, 0, 12, 0) };
        if (!d.IsLocal) glyph.Foreground = (Brush)FindResource("AccentBrush");
        var title = new TextBlock { Text = d.Label, Style = (Style)FindResource("Body") };
        var sub = new TextBlock
        {
            Text = d.IsLocal ? $"Kept in Clips\\{_moment?.Game.FolderName}" : $"Discord only · fits {d.Channel!.MaxUploadMB} MB · nothing kept here",
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
        var cb = _onPick;
        if (cb is null) return;
        if (RememberBox.IsChecked == true || d.Id != App.Current.Settings.Current.LastDestination)
            _controller.RememberDestination(d, RememberBox.IsChecked == true);
        _onPick = null;
        _onDismiss = null; // picking is not dismissing
        CloseAndRestore();
        cb(d);
    }

    private void Run(PanelAction action, Destination dest)
    {
        var m = _moment;
        _onDismiss = null;
        CloseAndRestore();
        if (m is null) return;
        _ = _controller.ExecuteAsync(m, action, dest);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (BackButton.Visibility != Visibility.Visible) return;
        _onPick = null;
        _onDismiss = null;
        DestinationStep.Visibility = Visibility.Collapsed;
        ActionsStep.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseAndRestore();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _restoreTo = IntPtr.Zero;
        CloseAndRestore(restoreFocus: false);
        App.Current.ShowMainWindow();
    }

    // ---- keyboard -----------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Escape)
        {
            if (DestinationStep.Visibility == Visibility.Visible && BackButton.Visibility == Visibility.Visible) Back_Click(this, new RoutedEventArgs());
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

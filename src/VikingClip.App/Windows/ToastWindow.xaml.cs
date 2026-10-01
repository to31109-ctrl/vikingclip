using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VikingClip.App.Services;
using VikingClip.Core.Native;

namespace VikingClip.App.Windows;

public enum ToastKind { Info, Success, Error, Progress }

/// <summary>Small bottom-right notification that never steals focus from the game.</summary>
public partial class ToastWindow : Window
{
    private readonly Func<RECT> _monitor;
    private readonly Func<bool> _enabled;
    private readonly DispatcherTimer _hide = new();
    private Action? _onClick;

    /// <summary>Dev snapshots: skip positioning, capture exclusion and motion.</summary>
    public bool SnapshotMode { get; set; }

    public ToastWindow(Func<RECT> monitorRect, Func<bool> enabled)
    {
        _monitor = monitorRect;
        _enabled = enabled;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            if (SnapshotMode) return;
            WindowNative.ExcludeFromCapture(this);
            WindowNative.MakeOverlay(this, noActivate: true);
        };
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
    }

    public void Show(ToastKind kind, string title, string? detail, Action? onClick = null, TimeSpan? duration = null)
    {
        Dispatcher.BeginInvoke(() => Present(kind, title, detail, null, onClick, duration ?? TimeSpan.FromSeconds(kind == ToastKind.Error ? 7 : 4.5)));
    }

    public void Progress(string title, string? detail, double? fraction = null)
    {
        Dispatcher.BeginInvoke(() => Present(ToastKind.Progress, title, detail, fraction, null, null));
    }

    public void HideToast() => Dispatcher.BeginInvoke(() => { _hide.Stop(); Hide(); });

    private void Present(ToastKind kind, string title, string? detail, double? fraction, Action? onClick, TimeSpan? autoHide)
    {
        if (!_enabled() && kind != ToastKind.Error) { Hide(); return; }
        _onClick = onClick;
        TitleText.Text = title;
        DetailText.Text = detail ?? "";
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        var (fg, well, glyph) = kind switch
        {
            ToastKind.Success => ("AccentBrush", "AccentDimBrush", ""),
            ToastKind.Error => ("DangerBrush", "DangerDimBrush", ""),
            ToastKind.Progress => ("TextMutedBrush", "Surface3Brush", ""),
            _ => ("TextBrush", "Surface3Brush", ""),
        };
        Glyph.Text = glyph;
        Glyph.Foreground = (Brush)FindResource(fg);
        GlyphWell.Background = (Brush)FindResource(well);
        ProgressBarEl.Visibility = kind == ToastKind.Progress ? Visibility.Visible : Visibility.Collapsed;
        ProgressBarEl.IsIndeterminate = kind == ToastKind.Progress && fraction is null;
        if (fraction is { } f) ProgressBarEl.Value = Math.Clamp(f, 0, 1);
        Cursor = onClick is null ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand;

        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            Opacity = SnapshotMode ? 1 : 0;
            Show();
        }
        UpdateLayout();
        if (!SnapshotMode)
        {
            WindowNative.PlaceAtCorner(this, _monitor(), WindowNative.Corner.BottomRight, 24);
            WindowNative.Topmost(this);
            if (!wasVisible) Enter();
        }

        _hide.Stop();
        if (autoHide is { } d)
        {
            _hide.Interval = d;
            _hide.Start();
        }
    }

    /// <summary>Fade + rise, exponential ease-out (the same motion language as the panel).</summary>
    private void Enter()
    {
        var slide = new TranslateTransform(0, 8);
        Root.RenderTransform = slide; // the Window itself cannot carry a RenderTransform
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var d = TimeSpan.FromMilliseconds(180);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, d) { EasingFunction = ease });
    }

    private void Root_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var a = _onClick;
        _hide.Stop();
        Hide();
        a?.Invoke();
    }
}

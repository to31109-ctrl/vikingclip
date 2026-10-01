using System.Windows;
using System.Windows.Media;
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

    /// <summary>Dev snapshots: skip positioning.</summary>
    public bool SnapshotMode { get; set; }

    public ToastWindow(Func<RECT> monitorRect, Func<bool> enabled)
    {
        _monitor = monitorRect;
        _enabled = enabled;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            if (SnapshotMode) return; // dev renders: plain invisible window, no capture exclusion
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
        var (bar, glyph) = kind switch
        {
            ToastKind.Success => ("AccentBrush", ""),
            ToastKind.Error => ("DangerBrush", ""),
            ToastKind.Progress => ("TextMutedBrush", ""),
            _ => ("TextMutedBrush", ""),
        };
        Bar.Background = (Brush)FindResource(bar);
        Glyph.Text = glyph;
        Glyph.Foreground = (Brush)FindResource(bar);
        ProgressBarEl.Visibility = kind == ToastKind.Progress ? Visibility.Visible : Visibility.Collapsed;
        ProgressBarEl.IsIndeterminate = kind == ToastKind.Progress && fraction is null;
        if (fraction is { } f) ProgressBarEl.Value = Math.Clamp(f, 0, 1);
        Cursor = onClick is null ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand;

        if (!IsVisible) Show();
        UpdateLayout();
        if (!SnapshotMode)
        {
            WindowNative.PlaceAtCorner(this, _monitor(), WindowNative.Corner.BottomRight, 24);
            WindowNative.Topmost(this);
        }

        _hide.Stop();
        if (autoHide is { } d)
        {
            _hide.Interval = d;
            _hide.Start();
        }
    }

    private void Root_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var a = _onClick;
        _hide.Stop();
        Hide();
        a?.Invoke();
    }
}

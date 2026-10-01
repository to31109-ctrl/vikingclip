using System.Windows;
using System.Windows.Threading;
using VikingClip.App.Services;
using VikingClip.Core.Capture;
using VikingClip.Core.Native;
using VikingClip.Core.Util;

namespace VikingClip.App.Windows;

/// <summary>"REC 1:23" badge in the corner of the recorded monitor. Excluded from the recording itself.</summary>
public partial class RecordingIndicatorWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private RecordingSession? _session;
    private bool _blink;

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowNative.ExcludeFromCapture(this);
            WindowNative.MakeOverlay(this, noActivate: true);
        };
        _timer.Tick += (_, _) =>
        {
            if (_session is null) return;
            TimeText.Text = "REC " + FileNames.HumanDuration(_session.Elapsed);
            _blink = !_blink;
            Dot.Opacity = _blink ? 1 : 0.35;
        };
    }

    public void ShowFor(RecordingSession session, RECT monitor)
    {
        _session = session;
        TimeText.Text = "REC 0:00";
        Show();
        UpdateLayout();
        WindowNative.PlaceAtCorner(this, monitor, WindowNative.Corner.TopRight, 16);
        WindowNative.Topmost(this);
        _timer.Start();
    }

    public void HideIndicator()
    {
        _timer.Stop();
        _session = null;
        Hide();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VikingClip.Core.Capture;
using VikingClip.Core.Settings;
using VikingClip.Core.Util;

namespace VikingClip.App.Pages;

public sealed record MonitorRow(string Title, string Detail, string State, Brush StateBrush);

public partial class CapturePage : UserControl, IPage
{
    private readonly App _app = App.Current;
    // true until Load() ran once: WPF fires ValueChanged/SelectionChanged while the XAML is still being built.
    private bool _loading = true;
    private AppSettings? _appliedSnapshot;

    public CapturePage()
    {
        InitializeComponent();
        Encoder.Items.Add(new ComboBoxItem { Content = "Auto (probe and pick the best)", Tag = "" });
        foreach (var p in EncoderPlan.All) Encoder.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p.Id });
        Loaded += (_, _) => { Load(); RefreshStatus(); };
        _app.Engine.Changed += () => Dispatcher.BeginInvoke(RefreshStatus);
        _appliedSnapshot = _app.Settings.Current.Clone();
    }

    public void OnShown()
    {
        Load();
        RefreshStatus();
    }

    private void Load()
    {
        _loading = true;
        var s = _app.Settings.Current;
        ClipLength.Value = s.ClipLengthSeconds;
        ClipLengthText.Text = s.ClipLengthSeconds < 60 ? $"{s.ClipLengthSeconds} s" : FileNames.HumanDuration(s.ClipLengthSeconds);
        Select(Quality, s.Capture.Quality.ToString());
        Select(Fps, s.Capture.Fps.ToString());
        Select(MaxHeight, s.Capture.MaxHeight.ToString());
        Bitrate.Text = s.Capture.BitrateKbps.ToString();
        Select(Monitors, s.Capture.Monitors.ToString());
        Select(Encoder, s.Capture.EncoderOverride ?? "");
        DrawCursor.IsChecked = s.Capture.DrawCursor;
        Indicator.IsChecked = s.Capture.ShowRecordingIndicator;
        var custom = s.Capture.Quality == QualityMode.Custom;
        MaxHeight.IsEnabled = custom;
        Bitrate.IsEnabled = custom;
        UpdateRam();
        _loading = false;
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = item; return; }
        }
        box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private void UpdateRam()
    {
        var s = _app.Settings.Current;
        long bytes = 0;
        foreach (var m in _app.Engine.Monitors)
        {
            var p = _app.Engine.ParamsFor(m);
            bytes += (long)((s.ClipLengthSeconds + 6) * p.BitrateKbps * 1000.0 / 8);
        }
        bytes += (long)((s.ClipLengthSeconds + 6) * 192000 * 2); // audio rings
        RamText.Text = _app.Engine.Monitors.Count == 0 ? "" : $"≈ {FileNames.HumanSize(bytes)} of RAM";
    }

    private void Save(Action<AppSettings> mutate)
    {
        if (_loading) return;
        var before = _app.Settings.Current;
        _app.Settings.Update(mutate);
        var after = _app.Settings.Current;
        if (before.ClipLengthSeconds != after.ClipLengthSeconds) _app.Engine.ApplyClipLength();
        RestartBar.Visibility = _appliedSnapshot is not null && CaptureEngine.RequiresRestart(_appliedSnapshot, after) ? Visibility.Visible : Visibility.Collapsed;
        UpdateRam();
    }

    private void ClipLength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        var v = (int)Math.Round(ClipLength.Value);
        ClipLengthText.Text = v < 60 ? $"{v} s" : FileNames.HumanDuration(v);
        Save(s => s.ClipLengthSeconds = v);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag?.ToString(), out var v)) ClipLength.Value = v;
    }

    private void Setting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Save(s =>
        {
            s.Capture.Quality = Enum.Parse<QualityMode>(TagOf(Quality));
            s.Capture.Fps = int.Parse(TagOf(Fps));
            s.Capture.MaxHeight = int.Parse(TagOf(MaxHeight));
            s.Capture.Monitors = Enum.Parse<MonitorMode>(TagOf(Monitors));
            s.Capture.EncoderOverride = string.IsNullOrEmpty(TagOf(Encoder)) ? null : TagOf(Encoder);
        });
        var custom = TagOf(Quality) == "Custom";
        MaxHeight.IsEnabled = custom;
        Bitrate.IsEnabled = custom;
    }

    private void Bitrate_Changed(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(Bitrate.Text.Trim(), out var kbps)) Save(s => s.Capture.BitrateKbps = Math.Clamp(kbps, 0, 200000));
        else Bitrate.Text = _app.Settings.Current.Capture.BitrateKbps.ToString();
    }

    private void Bitrate_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) Bitrate_Changed(sender, e);
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e) =>
        Save(s =>
        {
            s.Capture.DrawCursor = DrawCursor.IsChecked == true;
            s.Capture.ShowRecordingIndicator = Indicator.IsChecked == true;
        });

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        RestartBar.Visibility = Visibility.Collapsed;
        _appliedSnapshot = _app.Settings.Current.Clone();
        await _app.Engine.RestartAsync();
    }

    private void RefreshStatus()
    {
        var eng = _app.Engine;
        EngineText.Text = $"{eng.State} · {eng.StatusText}" + (string.IsNullOrEmpty(eng.FfmpegVersion) ? "" : $"\n{eng.FfmpegVersion}");
        var rows = new List<MonitorRow>();
        foreach (var c in eng.Captures)
        {
            var p = c.ActiveParams;
            var detail = $"{c.Plan.DisplayName} · {(p is null ? "" : $"{p.Fps} fps · {p.BitrateKbps / 1000.0:0.#} Mbps target")}" +
                         (c.MeasuredKbps > 0 ? $" · {c.MeasuredKbps / 1000.0:0.#} Mbps actual" : "") +
                         $" · buffer {c.Ring.BufferedSeconds:0} s / {FileNames.HumanSize(c.Ring.Bytes)}" +
                         (c.Restarts > 0 ? $" · {c.Restarts} restarts" : "") +
                         (c.LastError is not null && c.State != CaptureState.Running ? $"\n{c.LastError}" : "");
            var brush = c.State switch
            {
                CaptureState.Running => "AccentBrush",
                CaptureState.Failed => "DangerBrush",
                _ => "WarningBrush",
            };
            rows.Add(new MonitorRow(c.Monitor.Label, detail, c.State.ToString(), (Brush)FindResource(brush)));
        }
        MonitorList.ItemsSource = rows;
        WarningsText.Text = string.Join("\n", eng.Warnings);
        UpdateRam();
    }
}

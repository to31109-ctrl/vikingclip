using System.Windows;
using System.Windows.Controls;
using VikingClip.Core.Audio;
using VikingClip.Core.Capture;
using VikingClip.Core.Settings;

namespace VikingClip.App.Pages;

public partial class AudioPage : UserControl, IPage
{
    private readonly App _app = App.Current;
    private bool _loading = true; // handlers fire during XAML load; see CapturePage
    private AppSettings? _applied;

    public AudioPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
        _app.Engine.Changed += () => Dispatcher.BeginInvoke(RefreshStatus);
        _applied = _app.Settings.Current.Clone();
    }

    public void OnShown() => Load();

    private void Load()
    {
        _loading = true;
        var a = _app.Settings.Current.Audio;
        DesktopOn.IsChecked = a.CaptureDesktop;
        MicOn.IsChecked = a.CaptureMicrophone;
        DesktopGain.Value = Math.Round(a.DesktopGain * 100);
        MicGain.Value = Math.Round(a.MicrophoneGain * 100);
        Offset.Value = a.OffsetMs;
        DesktopGainText.Text = $"{DesktopGain.Value:0}%";
        MicGainText.Text = $"{MicGain.Value:0}%";
        OffsetText.Text = $"{a.OffsetMs:+0;-0;0} ms";

        MicDevice.Items.Clear();
        MicDevice.Items.Add(new ComboBoxItem { Content = "Windows default microphone", Tag = "" });
        foreach (var d in AudioDevices.ListMicrophones())
            MicDevice.Items.Add(new ComboBoxItem { Content = d.Name + (d.IsDefault ? "  (default)" : ""), Tag = d.Id });
        var want = a.MicrophoneDeviceId ?? "";
        MicDevice.SelectedIndex = 0;
        foreach (ComboBoxItem item in MicDevice.Items)
            if ((string)item.Tag == want) { MicDevice.SelectedItem = item; break; }
        _loading = false;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var d = _app.Engine.DesktopSource;
        var m = _app.Engine.MicSource;
        DesktopStatus.Text = d is null ? "off" : d.IsRunning ? $"● {d.DeviceName}" : $"not running: {d.LastError}";
        MicStatus.Text = m is null ? "off" : m.IsRunning ? $"● {m.DeviceName}" : $"not running: {m.LastError}";
    }

    private void Save(Action<AudioSettings> mutate)
    {
        if (_loading) return;
        _app.Settings.Update(s => mutate(s.Audio));
        RestartBar.Visibility = _applied is not null && CaptureEngine.RequiresRestart(_applied, _app.Settings.Current) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e) =>
        Save(a => { a.CaptureDesktop = DesktopOn.IsChecked == true; a.CaptureMicrophone = MicOn.IsChecked == true; });

    private void MicDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        var id = (MicDevice.SelectedItem as ComboBoxItem)?.Tag as string;
        Save(a => a.MicrophoneDeviceId = string.IsNullOrEmpty(id) ? null : id);
    }

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        DesktopGainText.Text = $"{DesktopGain.Value:0}%";
        MicGainText.Text = $"{MicGain.Value:0}%";
        Save(a => { a.DesktopGain = (float)(DesktopGain.Value / 100); a.MicrophoneGain = (float)(MicGain.Value / 100); });
    }

    private void Offset_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        OffsetText.Text = $"{Offset.Value:+0;-0;0} ms";
        Save(a => a.OffsetMs = (int)Offset.Value);
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        RestartBar.Visibility = Visibility.Collapsed;
        _applied = _app.Settings.Current.Clone();
        await _app.Engine.RestartAsync();
    }
}

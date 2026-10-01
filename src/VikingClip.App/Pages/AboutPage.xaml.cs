using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using VikingClip.App.Services;
using VikingClip.Core;
using VikingClip.Core.Capture;
using VikingClip.Core.Logging;
using VikingClip.Core.Util;

namespace VikingClip.App.Pages;

public partial class AboutPage : UserControl, IPage
{
    private readonly App _app = App.Current;

    public AboutPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        _app.Updates.Changed += () => Dispatcher.BeginInvoke(Refresh);
        _app.Engine.Changed += () => Dispatcher.BeginInvoke(Refresh);
    }

    public void OnShown() => Refresh();

    private void Refresh()
    {
        VersionText.Text = $"VikingClip v{_app.Updates.CurrentVersion}";
        UpdateStatus.Text = _app.Updates.Status;
        RestartButton.Visibility = _app.Updates.Downloaded is not null ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.IsEnabled = _app.Updates.IsInstalled;
        DiagText.Text = BuildDiagnostics();
    }

    private string BuildDiagnostics()
    {
        var e = _app.Engine;
        var sb = new StringBuilder();
        sb.AppendLine($"VikingClip v{_app.Updates.CurrentVersion} ({(_app.Updates.IsInstalled ? "installed" : "dev build")})");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} · {Environment.ProcessorCount} threads · {Environment.MachineName}");
        sb.AppendLine(e.FfmpegVersion);
        sb.AppendLine($"Engine: {e.State} · {e.StatusText}");
        sb.AppendLine($"RAM in replay buffers: {FileNames.HumanSize(e.EstimatedRamBytes)}");
        foreach (var c in e.Captures)
        {
            var p = c.ActiveParams;
            sb.AppendLine($"  {c.Monitor.Label} [{c.Monitor.AdapterName}] → {c.Plan.DisplayName} · {c.State}" +
                          (p is null ? "" : $" · {p.Fps} fps · {p.BitrateKbps} kbps") +
                          (c.MeasuredKbps > 0 ? $" · actual {c.MeasuredKbps:0} kbps" : "") +
                          $" · {c.Ring.BufferedSeconds:0} s buffered · exactTiming={c.HasExactTiming}" +
                          (c.Restarts > 0 ? $" · restarts {c.Restarts}" : "") +
                          (c.LastError is null ? "" : $" · last error: {c.LastError}"));
        }
        if (e.DesktopSource is { } d) sb.AppendLine($"  Desktop audio: {(d.IsRunning ? "running" : "stopped")} · {d.DeviceName} {d.LastError}");
        if (e.MicSource is { } m) sb.AppendLine($"  Microphone: {(m.IsRunning ? "running" : "stopped")} · {m.DeviceName} {m.LastError}");
        foreach (var w in e.Warnings) sb.AppendLine("  ! " + w);
        foreach (var h in _app.HotkeyProblems) sb.AppendLine("  ! " + h);
        sb.AppendLine($"Settings: {Paths.SettingsFile}");
        sb.AppendLine($"Logs: {Paths.LogsDir}");
        return sb.ToString().TrimEnd();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        await _app.Updates.CheckAsync(userInitiated: true);
        CheckButton.IsEnabled = _app.Updates.IsInstalled;
        Refresh();
    }

    private void RestartUpdate_Click(object sender, RoutedEventArgs e) => _app.Updates.ApplyAndRestart();

    private void Logs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.LogsDir}\"") { UseShellExecute = true });

    private void SettingsFile_Click(object sender, RoutedEventArgs e) => App.RevealInExplorer(Paths.SettingsFile);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var recent = string.Join("\n", Log.RecentEntries.TakeLast(60).Select(l => $"{l.Time:HH:mm:ss} [{l.Level}] {l.Message}"));
            Clipboard.SetText(BuildDiagnostics() + "\n\n--- recent log ---\n" + recent);
        }
        catch { }
    }

    private void GitHub_Click(object sender, RoutedEventArgs e) => App.OpenUrl(UpdateService.RepoUrl);
    private void Contribute_Click(object sender, RoutedEventArgs e) => App.OpenUrl(UpdateService.RepoUrl + "#contributing");
}

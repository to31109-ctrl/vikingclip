using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VikingClip.Core.Settings;
using VikingClip.Core.Startup;
using VikingClip.Core.Util;

namespace VikingClip.App.Pages;

public sealed record UsageRow(string Game, double Fraction, string Text);

public partial class StoragePage : UserControl, IPage
{
    private readonly App _app = App.Current;
    private bool _loading;

    public StoragePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    public void OnShown() => Load();

    private void Load()
    {
        _loading = true;
        var s = _app.Settings.Current;
        RootBox.Text = s.ClipsRoot;
        LaunchAtLogin.IsChecked = s.LaunchAtLogin;
        StartMinimized.IsChecked = s.StartMinimized;
        ShowToasts.IsChecked = s.ShowToasts;
        CheckUpdates.IsChecked = s.CheckForUpdates;
        Remember.IsChecked = s.RememberDestination;
        RememberText.Text = s.LastDestination is null ? "" : $"last: {_app.Actions.DefaultDestination().Label}";
        _loading = false;
        RenderOverrides();
        _ = LoadUsageAsync(s.ClipsRoot);
    }

    private async Task LoadUsageAsync(string root)
    {
        var rows = await Task.Run(() =>
        {
            var list = new List<(string Game, long Bytes, int Count)>();
            if (!Directory.Exists(root)) return list;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                long bytes = 0;
                var count = 0;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try { bytes += new FileInfo(f).Length; count++; } catch { }
                }
                if (count > 0) list.Add((Path.GetFileName(dir), bytes, count));
            }
            return list.OrderByDescending(x => x.Bytes).ToList();
        });
        var max = rows.Count == 0 ? 1 : rows.Max(r => r.Bytes);
        UsageList.ItemsSource = rows.Select(r => new UsageRow(r.Game, (double)r.Bytes / max, $"{r.Count} files · {FileNames.HumanSize(r.Bytes)}")).ToList();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where clips are saved", InitialDirectory = _app.Settings.Current.ClipsRoot };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            _app.Settings.Update(s => s.ClipsRoot = dlg.FolderName);
            Load();
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _app.OpenClipsFolder();

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.Update(s =>
        {
            s.LaunchAtLogin = LaunchAtLogin.IsChecked == true;
            s.StartMinimized = StartMinimized.IsChecked == true;
            s.ShowToasts = ShowToasts.IsChecked == true;
            s.CheckForUpdates = CheckUpdates.IsChecked == true;
            s.RememberDestination = Remember.IsChecked == true;
        });
        if (_app.Updates.IsInstalled) AutoStart.Set(_app.Settings.Current.LaunchAtLogin, Program.LauncherExePath);
        _app.Updates.Start(_app.Settings.Current.CheckForUpdates);
    }

    private void RenderOverrides()
    {
        OverrideList.Items.Clear();
        foreach (var kv in _app.Settings.Current.GameOverrides.OrderBy(k => k.Key))
        {
            var exe = kv.Key;
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var a = new TextBlock { Text = exe, Style = (Style)FindResource("Body"), VerticalAlignment = VerticalAlignment.Center, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), FontSize = 12 };
            var b = new TextBlock { Text = kv.Value.NotAGame ? "not a game → Desktop folder" : $"→ {kv.Value.Name}", Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
            var rm = new Button { Style = (Style)FindResource("GhostButton"), Content = "Remove", Padding = new Thickness(8, 3, 8, 3) };
            rm.Click += (_, _) => { _app.Settings.Update(s => s.GameOverrides.Remove(exe)); RenderOverrides(); };
            Grid.SetColumn(b, 1);
            Grid.SetColumn(rm, 2);
            grid.Children.Add(a);
            grid.Children.Add(b);
            grid.Children.Add(rm);
            OverrideList.Items.Add(grid);
        }
        ForegroundHint.Text = _app.Settings.Current.GameOverrides.Count == 0 ? "No overrides yet." : "";
    }

    private void AddOverride_Click(object sender, RoutedEventArgs e)
    {
        var exe = NewExe.Text.Trim();
        if (exe.Length == 0) return;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        var notGame = NewNotGame.IsChecked == true;
        var name = NewGameName.Text.Trim();
        if (!notGame && name.Length == 0) { ForegroundHint.Text = "Enter the folder name to use, or tick 'Not a game'."; return; }
        _app.Settings.Update(s => s.GameOverrides[exe] = new GameOverride { Name = notGame ? null : name, NotAGame = notGame });
        NewExe.Text = "";
        NewGameName.Text = "";
        NewNotGame.IsChecked = false;
        RenderOverrides();
    }
}

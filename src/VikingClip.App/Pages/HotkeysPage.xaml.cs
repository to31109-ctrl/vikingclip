using System.Windows;
using System.Windows.Controls;
using VikingClip.App.Controls;
using VikingClip.Core.Settings;

namespace VikingClip.App.Pages;

public partial class HotkeysPage : UserControl, IPage
{
    private readonly App _app = App.Current;

    public HotkeysPage()
    {
        InitializeComponent();
        Build();
        _app.HotkeysChanged += () => Dispatcher.BeginInvoke(ShowProblems);
    }

    public void OnShown()
    {
        Build();
        ShowProblems();
    }

    private void Build()
    {
        Rows.Children.Clear();
        var h = _app.Settings.Current.Hotkeys;
        AddRow("Open the panel", "Clip · Record · Screenshot, with a choice of where to save.", h.Panel, v => s => s.Hotkeys.Panel = v);
        AddRow("Instant clip", "Saves the last seconds straight to your last-used destination, no panel.", h.InstantClip, v => s => s.Hotkeys.InstantClip = v);
        AddRow("Start / stop recording", "Toggles a manual recording of the monitor you're on.", h.ToggleRecording, v => s => s.Hotkeys.ToggleRecording = v);
        AddRow("Screenshot", "Saves a screenshot to your last-used destination.", h.Screenshot, v => s => s.Hotkeys.Screenshot = v, last: true);
        ShowProblems();
    }

    private void AddRow(string title, string description, string current, Func<string, Action<AppSettings>> save, bool last = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, last ? 0 : 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Body"), FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 2, 0, 0) });
        var editor = new HotkeyEditor { Value = Hotkey.Parse(current), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        editor.ValueChanged += hk => _app.Settings.Update(save(hk.ToString()));
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
        Rows.Children.Add(grid);
    }

    private void ShowProblems() => Problems.Text = _app.HotkeyProblems.Count == 0 ? "" : "⚠ " + string.Join("\n⚠ ", _app.HotkeyProblems);
}

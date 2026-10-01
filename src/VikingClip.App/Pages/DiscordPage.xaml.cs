using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VikingClip.Core.Discord;
using VikingClip.Core.Settings;

namespace VikingClip.App.Pages;

public partial class DiscordPage : UserControl, IPage
{
    private readonly App _app = App.Current;

    public DiscordPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
    }

    public void OnShown() => Render();

    private void Render()
    {
        ChannelList.Items.Clear();
        foreach (var ch in _app.Settings.Current.DiscordChannels)
            ChannelList.Items.Add(BuildCard(ch));
    }

    private Style S(string key) => (Style)FindResource(key);

    private UIElement BuildCard(DiscordChannel ch)
    {
        var card = new Border { Style = S("Card"), Margin = new Thickness(0, 0, 0, 14) };
        var root = new StackPanel();
        card.Child = root;

        var header = new Grid();
        var glyph = new TextBlock { Text = "", Style = S("Icon"), Foreground = (Brush)FindResource("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 10, 0) };
        var name = new TextBox { Text = ch.Name, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(26, 0, 0, 0), ToolTip = "Name shown in the panel" };
        name.LostFocus += (_, _) => Update(ch.Id, c => c.Name = name.Text.Trim().Length == 0 ? c.Name : name.Text.Trim());
        var remove = new Button { Style = S("DangerButton"), Content = "Remove", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(10, 4, 10, 4) };
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(Window.GetWindow(this), $"Remove {ch.Name}?", "VikingClip", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _app.Settings.Update(s => s.DiscordChannels.RemoveAll(c => c.Id == ch.Id));
            Render();
        };
        header.Children.Add(glyph);
        header.Children.Add(name);
        header.Children.Add(remove);
        root.Children.Add(header);

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 14, 0, 0) };

        var limitPanel = new StackPanel { Margin = new Thickness(0, 0, 10, 12) };
        limitPanel.Children.Add(new TextBlock { Text = "Server upload limit", Style = S("Label") });
        var limit = new ComboBox();
        foreach (var (label, mb) in new[] { ("10 MB (no boost)", 10), ("25 MB", 25), ("50 MB (boost level 2)", 50), ("100 MB (boost level 3)", 100), ("500 MB", 500) })
            limit.Items.Add(new ComboBoxItem { Content = label, Tag = mb, IsSelected = mb == ch.MaxUploadMB });
        if (limit.SelectedItem is null) limit.SelectedIndex = 0;
        limit.SelectionChanged += (_, _) => Update(ch.Id, c => c.MaxUploadMB = (int)((ComboBoxItem)limit.SelectedItem).Tag);
        limitPanel.Children.Add(limit);
        grid.Children.Add(limitPanel);

        var posterPanel = new StackPanel { Margin = new Thickness(10, 0, 0, 12) };
        posterPanel.Children.Add(new TextBlock { Text = "Post as (optional name)", Style = S("Label") });
        var poster = new TextBox { Text = ch.PosterName ?? "" };
        poster.LostFocus += (_, _) => Update(ch.Id, c => c.PosterName = string.IsNullOrWhiteSpace(poster.Text) ? null : poster.Text.Trim());
        posterPanel.Children.Add(poster);
        grid.Children.Add(posterPanel);
        root.Children.Add(grid);

        root.Children.Add(new TextBlock { Text = "Message  ({game} {kind} {length} {user} are filled in)", Style = S("Label") });
        var template = new TextBox { Text = ch.MessageTemplate };
        template.LostFocus += (_, _) => Update(ch.Id, c => c.MessageTemplate = template.Text);
        root.Children.Add(template);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var status = new TextBlock { Style = S("Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var test = new Button { Content = "Test connection", Padding = new Thickness(10, 4, 10, 4) };
        test.Click += async (_, _) =>
        {
            status.Text = "Checking…";
            try
            {
                var info = await DiscordWebhook.GetInfoAsync(ch.GetWebhookUrl());
                status.Text = $"✓ Connected · webhook \"{info.Name}\"";
                status.Foreground = (Brush)FindResource("AccentBrush");
            }
            catch (Exception ex)
            {
                status.Text = "✗ " + ex.Message;
                status.Foreground = (Brush)FindResource("DangerBrush");
            }
        };
        var replace = new Button { Content = "Replace URL…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        replace.Click += (_, _) =>
        {
            var box = new PasswordBox { Width = 420 };
            var dlg = new Window
            {
                Title = "New webhook URL",
                Owner = Window.GetWindow(this),
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Style = S("DarkWindow"),
            };
            var ok = new Button { Style = S("PrimaryButton"), Content = "Save", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            ok.Click += (_, _) =>
            {
                if (!DiscordWebhook.TryNormalizeUrl(box.Password, out var url)) { MessageBox.Show(dlg, "That is not a Discord webhook URL.", "VikingClip"); return; }
                Update(ch.Id, c => c.SetWebhookUrl(url));
                dlg.Close();
            };
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "Paste the new webhook URL", Style = S("Label") });
            panel.Children.Add(box);
            panel.Children.Add(ok);
            dlg.Content = panel;
            dlg.SourceInitialized += (_, _) => Services.WindowNative.ApplyDarkTitleBar(dlg);
            dlg.ShowDialog();
        };
        actions.Children.Add(test);
        actions.Children.Add(replace);
        actions.Children.Add(status);
        root.Children.Add(actions);
        return card;
    }

    private void Update(Guid id, Action<DiscordChannel> mutate) =>
        _app.Settings.Update(s =>
        {
            var c = s.DiscordChannels.FirstOrDefault(x => x.Id == id);
            if (c is not null) mutate(c);
        });

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = NewName.Text.Trim();
        if (name.Length == 0) { AddStatus.Text = "Give the channel a name (e.g. #clips)."; return; }
        if (!DiscordWebhook.TryNormalizeUrl(NewUrl.Password, out var url))
        {
            AddStatus.Text = "That doesn't look like a Discord webhook URL (https://discord.com/api/webhooks/…).";
            return;
        }
        AddStatus.Text = "Checking the webhook…";
        try
        {
            var info = await DiscordWebhook.GetInfoAsync(url);
            var ch = new DiscordChannel
            {
                Name = name,
                MaxUploadMB = int.Parse((string)((ComboBoxItem)NewLimit.SelectedItem).Tag),
            };
            ch.SetWebhookUrl(url);
            _app.Settings.Update(s => s.DiscordChannels.Add(ch));
            AddStatus.Text = $"Added · webhook \"{info.Name}\" works.";
            NewName.Text = "";
            NewUrl.Password = "";
            Render();
        }
        catch (Exception ex)
        {
            AddStatus.Text = "Discord rejected it: " + ex.Message;
        }
    }
}

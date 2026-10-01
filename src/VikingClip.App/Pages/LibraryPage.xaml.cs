using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VikingClip.Core.Library;
using VikingClip.Core.Util;

namespace VikingClip.App.Pages;

public sealed class ClipItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    public required ClipEntry Entry { get; init; }
    public string Game => Entry.Game;
    public DateTime Created => Entry.Created;
    public long Bytes => Entry.Bytes;
    public string KindText => Entry.Meta.Kind.ToString();
    public string KindGlyph => Entry.Meta.Kind switch { MediaKind.Screenshot => "", MediaKind.Recording => "", _ => "" };
    public string? DurationText => Entry.Meta.DurationSeconds > 0 ? FileNames.HumanDuration(Entry.Meta.DurationSeconds) : null;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class LibraryPage : UserControl, IPage
{
    private readonly App _app = App.Current;
    private readonly ObservableCollection<ClipItem> _items = new();
    private List<ClipEntry> _all = new();
    private readonly SemaphoreSlim _thumbGate = new(2);
    private bool _loading;
    private string _filter = "";

    public LibraryPage()
    {
        InitializeComponent();
        Items.ItemsSource = _items;
        Loaded += (_, _) => Refresh();
        _app.Actions.LibraryChanged += () => Dispatcher.BeginInvoke(Refresh);
    }

    public void OnShown() => Refresh();

    private void Refresh()
    {
        var root = _app.Settings.Current.ClipsRoot;
        _all = _app.Library.Scan(root);
        var games = _all.Select(e => e.Game).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g).ToList();

        _loading = true;
        var selected = _filter;
        GameFilter.Items.Clear();
        GameFilter.Items.Add("All games");
        foreach (var g in games) GameFilter.Items.Add(g);
        GameFilter.SelectedIndex = Math.Max(0, GameFilter.Items.IndexOf(selected));
        _loading = false;

        Apply();
        var total = _all.Sum(e => e.Bytes);
        SubtitleText.Text = _all.Count == 0 ? "Clips, recordings and screenshots" : $"{_all.Count} items · {FileNames.HumanSize(total)}";
        EmptyHint.Text = $"Press {_app.Settings.Current.Hotkeys.Panel} in a game and hit Clip.";
    }

    private void Apply()
    {
        var list = string.IsNullOrEmpty(_filter) || _filter == "All games" ? _all : _all.Where(e => e.Game.Equals(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _items.Clear();
        foreach (var e in list) _items.Add(new ClipItem { Entry = e });
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _items.ToList()) _ = LoadThumbAsync(item);
    }

    private async Task LoadThumbAsync(ClipItem item)
    {
        await _thumbGate.WaitAsync();
        try
        {
            if (!_items.Contains(item)) return;
            await _app.Library.FillDurationAsync(item.Entry);
            var path = await _app.Library.EnsureThumbnailAsync(item.Entry);
            if (path is null) return;
            var img = await Task.Run(() =>
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 400;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                return bi;
            });
            item.Thumbnail = img;
        }
        catch { }
        finally { _thumbGate.Release(); }
    }

    private void GameFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _filter = GameFilter.SelectedItem as string ?? "";
        Apply();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _app.OpenClipsFolder();

    private static ClipItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipItem;

    private void Thumb_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is { } item) App.OpenWithDefaultApp(item.Entry.Path);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) App.OpenWithDefaultApp(item.Entry.Path);
    }

    private void Discord_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var channels = _app.Settings.Current.DiscordChannels.Where(c => !string.IsNullOrEmpty(c.WebhookProtected)).ToList();
        var menu = new ContextMenu();
        if (channels.Count == 0)
        {
            var mi = new MenuItem { Header = "Add a Discord channel first…" };
            mi.Click += (_, _) => _app.MainWindowInstance?.NavigateTo("discord");
            menu.Items.Add(mi);
        }
        foreach (var ch in channels)
        {
            var mi = new MenuItem { Header = $"Send to {ch.Name}" };
            mi.Click += (_, _) => _ = _app.Actions.SendExistingToDiscordAsync(item.Entry.Path, ch, item.Entry.Meta, item.Game);
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var menu = new ContextMenu();
        var reveal = new MenuItem { Header = "Show in folder" };
        reveal.Click += (_, _) => App.RevealInExplorer(item.Entry.Path);
        var copy = new MenuItem { Header = "Copy file path" };
        copy.Click += (_, _) => { try { Clipboard.SetText(item.Entry.Path); } catch { } };
        var del = new MenuItem { Header = "Delete" };
        del.Click += (_, _) =>
        {
            if (MessageBox.Show(Window.GetWindow(this), $"Delete {Path.GetFileName(item.Entry.Path)}?", "VikingClip", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                _app.Library.Delete(item.Entry);
                _items.Remove(item);
                _all.Remove(item.Entry);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Could not delete: " + ex.Message, "VikingClip");
            }
        };
        menu.Items.Add(reveal);
        menu.Items.Add(copy);
        menu.Items.Add(new Separator());
        menu.Items.Add(del);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MapleLite.Host.Pages;
using MapleLite.Host.Plugins;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host;

internal sealed class MainWindow : Window
{
    readonly Shell _shell;
    readonly ListBox _nav = new() { Background = Brushes.Transparent };
    readonly ContentControl _content = new();
    readonly TextBlock _toast;
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    readonly Dictionary<string, Control> _pages = new();

    /// <summary>A sidebar entry. Plugin pages are cached per plugin instance (PageKey), so a restarted plugin gets a new page.</summary>
    sealed record NavItem(string Title, string Key, PluginEntry? Plugin = null)
    {
        public string PageKey => Plugin == null ? Key : $"{Key}#{Plugin.Generation}";
        public override string ToString() => Title;
    }

    public MainWindow(Shell shell)
    {
        _shell = shell;
        Title = "MapleLite";
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MapleLite/Assets/MapleLite.ico")));
        Width = 1180; Height = 780; MinWidth = 760; MinHeight = 520;
        this.Res(BackgroundProperty, ThemeTokens.Background);

        var brand = Ui.Stack(2,
            Ui.Text("MapleLite", ThemeTokens.Accent, 20, FontWeight.Bold),
            Ui.Muted(""));
        brand.Margin = new Thickness(16, 18, 16, 14);

        _toast = Ui.Muted("");
        _toast.Margin = new Thickness(16, 8, 16, 14);
        _toastTimer.Tick += (_, _) => { _toast.Text = ""; _toastTimer.Stop(); };

        var sidebarGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        sidebarGrid.Children.Add(brand);
        Grid.SetRow(_nav, 1); sidebarGrid.Children.Add(_nav);
        Grid.SetRow(_toast, 2); sidebarGrid.Children.Add(_toast);
        var sidebar = new Border { Child = sidebarGrid, BorderThickness = new Thickness(0, 0, 1, 0) }
            .Res(Border.BackgroundProperty, ThemeTokens.SurfaceAlt).Res(Border.BorderBrushProperty, ThemeTokens.Border);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*") };
        root.Children.Add(sidebar);
        Grid.SetColumn(_content, 1); root.Children.Add(_content);
        Content = root;

        _nav.SelectionChanged += (_, _) => { if (_nav.SelectedItem is NavItem n) Show(n); };
        shell.Notified += m => Dispatcher.UIThread.Post(() => { _toast.Text = m; _toastTimer.Stop(); _toastTimer.Start(); });
        shell.Plugins.Changed += () => Dispatcher.UIThread.Post(RefreshNav);
        RefreshNav();
    }

    /// <summary>Rebuilds the sidebar from the running plugins, keeping the current page when it still exists.</summary>
    public void RefreshNav()
    {
        var items = new List<NavItem> { new("Game", "game") };
        items.AddRange(_shell.Plugins.Entries.Where(p => p.Running).Select(p => new NavItem(p.Manifest.Name, "plugin:" + p.Manifest.Id, p)));
        items.Add(new("Settings", "settings"));
        // forget pages of plugins that stopped or restarted
        var live = items.Select(i => i.PageKey).ToHashSet();
        foreach (var key in _pages.Keys.Where(k => k.StartsWith("plugin:") && !live.Contains(k)).ToList()) _pages.Remove(key);
        var selected = (_nav.SelectedItem as NavItem)?.Key ?? "game";
        _nav.ItemsSource = items;
        _nav.SelectedItem = items.FirstOrDefault(i => i.Key == selected) ?? items[0];
    }

    /// <summary>Opens a page by key: "game", "settings", or a plugin id.</summary>
    public void Navigate(string key)
    {
        if (_nav.ItemsSource is IEnumerable<NavItem> items)
            _nav.SelectedItem = items.FirstOrDefault(i => i.Key == key || i.Key == "plugin:" + key) ?? _nav.SelectedItem;
    }

    /// <summary>Test switch: render the window to a PNG once the page has had time to fill, then quit.</summary>
    public void SnapshotAndExit(string path, Action quit)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Options.SnapshotWait) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using (var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size))
            {
                bmp.Render(this);
                bmp.Save(path);
            }
            quit(); // the normal way out, so plugins save as they would when the user closes the window
        };
        timer.Start();
    }

    void Show(NavItem item)
    {
        if (!_pages.TryGetValue(item.PageKey, out var page))
        {
            page = item.Key switch
            {
                "game" => new GamePage(_shell),
                "settings" => new SettingsPage(_shell),
                _ => PluginPage.Create(item.Plugin!),
            };
            _pages[item.PageKey] = page;
        }
        _content.Content = page;
    }
}

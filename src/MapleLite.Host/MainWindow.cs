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
    readonly Dictionary<object, Control> _pages = new();

    sealed record NavItem(string Title, string Key, LoadedPlugin? Plugin = null) { public override string ToString() => Title; }

    public MainWindow(Shell shell)
    {
        _shell = shell;
        Title = "MapleLite";
        Width = 1180; Height = 780; MinWidth = 760; MinHeight = 520;
        this.Res(BackgroundProperty, ThemeTokens.Background);

        var brand = Ui.Stack(2,
            Ui.Text("MapleLite", ThemeTokens.Accent, 20, FontWeight.Bold),
            Ui.Muted("Companion for MapleStory Classic World"));
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
        RefreshNav();
    }

    public void RefreshNav()
    {
        var items = new List<NavItem> { new("Game", "game") };
        items.AddRange(_shell.Plugins.Plugins.Select(p => new NavItem(p.Plugin.Manifest.Name, "plugin:" + p.Plugin.Manifest.Id, p)));
        items.Add(new("Settings", "settings"));
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
    public void SnapshotAndExit(string path, Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using (var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size))
            {
                bmp.Render(this);
                bmp.Save(path);
            }
            desktop.Shutdown();
        };
        timer.Start();
    }

    void Show(NavItem item)
    {
        if (!_pages.TryGetValue(item.Key, out var page))
        {
            page = item.Key switch
            {
                "game" => new GamePage(_shell),
                "settings" => new SettingsPage(_shell),
                _ => PluginPage.Create(item.Plugin!),
            };
            _pages[item.Key] = page;
        }
        _content.Content = page;
    }
}

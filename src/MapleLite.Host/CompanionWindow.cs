using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MapleLite.Core.Platform;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using MapleLite.Host.Pages;

namespace MapleLite.Host;

/// <summary>A normal interactive window beside the game, never embedded in its process.</summary>
internal sealed class CompanionWindow : Window
{
    readonly Shell _shell;
    readonly ComboBox _plugins = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    readonly ContentControl _body = new();
    readonly TextBlock _status = Ui.Muted("");
    readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Dictionary<string, Control> _panels = new();
    bool _bottom;
    readonly CheckBox _track = new() { Content = "Follow game", IsChecked = true };

    public CompanionWindow(Shell shell)
    {
        _shell = shell;
        Title = "MapleLite companion";
        Width = 360; Height = 650; MinWidth = 280; MinHeight = 180;
        this.Res(BackgroundProperty, ThemeTokens.Background);
        var controls = Ui.Row(8, _track, Ui.Button("Side / bottom", () =>
        {
            _bottom = !_bottom;
            Width = _bottom ? 800 : 360; Height = _bottom ? 260 : 650;
            Follow();
        }));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(8) };
        root.Children.Add(controls);
        Grid.SetRow(_plugins, 1); root.Children.Add(_plugins);
        Grid.SetRow(_status, 2); root.Children.Add(_status);
        Grid.SetRow(_body, 3); root.Children.Add(_body);
        Content = root;
        _plugins.SelectionChanged += (_, _) => ShowPanel();
        _follow.Tick += (_, _) => Follow();
        shell.Plugins.Changed += PluginsChanged;
        Opened += (_, _) => { Refresh(); _follow.Start(); Follow(); };
        Closed += (_, _) => { _follow.Stop(); shell.Plugins.Changed -= PluginsChanged; _body.Content = null; _panels.Clear(); };
    }

    void PluginsChanged() => Dispatcher.UIThread.Post(Refresh);
    void Refresh()
    {
        var selected = (_plugins.SelectedItem as ComboBoxItem)?.Tag as string;
        var entries = _shell.Plugins.Entries.Where(p => p.Running && p.Instance is ICompanionPanel).ToList();
        var keys = entries.Select(p => $"{p.Manifest.Id}#{p.Generation}").ToHashSet();
        _body.Content = null;
        foreach (var key in _panels.Keys.Where(k => !keys.Contains(k)).ToList()) _panels.Remove(key);
        _plugins.ItemsSource = entries.Select(p => new ComboBoxItem { Content = p.Manifest.Name, Tag = $"{p.Manifest.Id}#{p.Generation}" }).ToList();
        _plugins.SelectedItem = _plugins.Items.Cast<ComboBoxItem>().FirstOrDefault(p => Equals(p.Tag, selected));
        if (_plugins.SelectedItem == null && entries.Count > 0) _plugins.SelectedIndex = 0;
        ShowPanel();
    }

    void ShowPanel()
    {
        if (_plugins.SelectedItem is not ComboBoxItem { Tag: string key }) { _body.Content = Ui.Muted("Enable Loot Logger, XP Tracker or Chat History in Settings."); return; }
        if (!_panels.TryGetValue(key, out var panel))
        {
            var entry = _shell.Plugins.Entries.FirstOrDefault(p => p.Running && $"{p.Manifest.Id}#{p.Generation}" == key);
            if (entry?.Instance is not ICompanionPanel plugin) return;
            try { panel = plugin.CreateCompanionPanel(); }
            catch (Exception e) { panel = Ui.Muted("Couldn't open panel: " + e.Message); }
            _panels[key] = panel;
        }
        _body.Content = panel;
    }

    void Follow()
    {
        if (_track.IsChecked != true) { _status.Text = "Free placement"; return; }
        var game = _shell.Capture.Status.Window;
        if (game?.Mode != WindowMode.Windowed) { _status.Text = "Switch the game to windowed mode to dock."; return; }
        var screen = Screens.ScreenFromPoint(new PixelPoint(game.Bounds.X, game.Bounds.Y));
        if (screen == null) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        int width = (int)Math.Ceiling(Width * scale), height = (int)Math.Ceiling(Height * scale);
        int x = _bottom ? game.Bounds.X : game.Bounds.Right + 6;
        int y = _bottom ? game.Bounds.Bottom + 6 : game.Bounds.Y;
        if (!_bottom && x + width > area.Right) x = game.Bounds.X - width - 6;
        if (x < area.X || y < area.Y || x + width > area.Right || y + height > area.Bottom)
        {
            _status.Text = "Move or shrink the game to leave room for this panel.";
            return;
        }
        _status.Text = _bottom ? "Below game" : "Beside game";
        var position = new PixelPoint(x, y);
        if (Position != position) Position = position;
    }
}

using MapleLite.Core.Game;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.SaleCard;

/// <summary>
/// The Shop Card page: the Selling list (tabs as in the game, search, filters), the Buying list (the item database), the card preview, and the Card Design and
/// Settings panels, which take the list's place while the preview stays in view.
/// </summary>
internal sealed class SaleCardPanel : UserControl
{
    const string ShowAll = "All items", ShowOn = "On the card", ShowOff = "Removed";
    const double BodyHeight = 640;

    readonly SaleCardEngine _engine;
    readonly IPluginContext _ctx;
    Session S => _engine.S;
    readonly ObservableCollection<SaleItem> _shown = new();
    readonly ItemsControl _list = new();
    readonly TextBlock _empty = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(30), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _search = new() { Watermark = "Search", Width = 190 };
    readonly ComboBox _show = new() { Width = 170 };
    readonly WrapPanel _tabs = new();
    readonly WrapPanel _kinds = new();
    readonly TextBlock _shopNote = Muted("Saved shop listings: scroll your own shop to read more. Uncheck sold or outdated items before sharing.");
    readonly Image _preview = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _status = Muted(""), _browserStatus = Muted(""), _gameLine = Muted(""), _sellerLine = Text("", ThemeTokens.Text, 16, FontWeight.SemiBold);
    readonly TabControl _sections = new();
    readonly ToggleSwitch _prices = new() { OnContent = "Prices shown", OffContent = "Prices hidden" };
    readonly ToggleButton _sellingOn = new() { Content = "Selling" }, _buyingOn = new() { Content = "Buying" };
    readonly Slider _priceSlider = new() { Minimum = 1, Maximum = 200, Value = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 150, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _pricePercent = new() { Content = "100%", MinWidth = 58, Padding = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer _render;
    (MapleLite.Core.Imaging.Frame frame, Avalonia.Media.Imaging.Bitmap bitmap)? _card;
    bool _loading;   // filling controls from the session: their change handlers must not write back

    // Card Design controls that the monitor can change (IGN, channel, location).
    readonly TextBox _seller = new(), _channel = new(), _server = new(), _tagline = new(), _location = new(), _title = new(), _discord = new() { Watermark = "your Discord name" };

    public SaleCardPanel(SaleCardEngine engine, IPluginContext ctx)
    {
        _engine = engine;
        _ctx = ctx;
        _render = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _render!.Stop(); RenderCard(); });

        var copy = Button("Copy image", async () => await CopyAsync(), accent: true);
        var save = Button("Save PNG…", async () => await SaveAsync());
        _prices.IsCheckedChanged += (_, _) => { if (_loading) return; S.ShowPrices = _prices.IsChecked == true; S.HidePrices = !S.PricesShown; _engine.SettingsChanged(); };
        ToolTip.SetTip(_prices, "Show or leave out the prices on the card. Your prices stay in the list either way.");

        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Children = { _sellerLine, _gameLine } };
        // one click puts every selling item, or every item you're buying, on the card or takes them all off
        _sellingOn.IsCheckedChanged += (_, _) => { if (_loading) return; S.HideSelling = _sellingOn.IsChecked != true; _engine.SettingsChanged(); };
        _buyingOn.IsCheckedChanged += (_, _) => { if (_loading) return; S.HideBuying = _buyingOn.IsChecked != true; _engine.SettingsChanged(); };
        ToolTip.SetTip(_sellingOn, "On: the items you're selling (the Selling tab) are on the card. Off: none of them are. Your ticks are kept either way.");
        ToolTip.SetTip(_buyingOn, "On: the items you're buying (the Buying tab) are on the card. Off: none of them are. Your ticks are kept either way.");
        var onCard = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "On the card:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }.Themed(TextBlock.ForegroundProperty, ThemeTokens.TextMuted), _sellingOn, _buyingOn } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { onCard, _prices, save, copy } };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        top.Children.Add(who);

        _sections.Items.Add(new TabItem { Header = "Selling", Content = BuildList() });
        _sections.Items.Add(new TabItem { Header = "Buying", Content = BuildBuying() });
        _sections.Items.Add(new TabItem { Header = "Design", Content = new ScrollViewer { Content = BuildDesign() } });
        _sections.Items.Add(new TabItem { Header = "Settings", Content = new ScrollViewer { Content = BuildSettings() } });
        // lists saved before the Buying tab came between Selling and Design: Design and Settings moved one along
        if (_engine.S.SectionV < 2) { if (_engine.S.Section >= 1) _engine.S.Section++; _engine.S.SectionV = 2; }
        _sections.SelectedIndex = Math.Clamp(_engine.S.Section, 0, 3);
        _sections.SelectionChanged += (_, _) => { if (_sections.SelectedIndex >= 0 && _engine.S.Section != _sections.SelectedIndex) { _engine.S.Section = _sections.SelectedIndex; _engine.SettingsChanged(); } };
        _sections.Height = BodyHeight;

        var maximize = Button("⛶  Maximize", ToggleMaximized);
        _maximize = maximize;
        ToolTip.SetTip(maximize, "Shows the card across the whole page, as large as it fits. Press Reduce (or Esc) to bring the lists back.");
        // a bar above the card, so the button never covers part of it
        var previewBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(maximize, Dock.Right);
        previewBar.Children.Add(maximize);
        // the price slider: every price on the card (and the total) at 1% to 200% of the list's; clicking the figure puts it back to 100%
        _priceSlider.ValueChanged += (_, _) =>
        {
            int pct = (int)Math.Round(_priceSlider.Value);
            _pricePercent.Content = pct + "%";
            if (_loading || pct == S.PricePercent) return;
            S.PricePercent = pct; _engine.SettingsChanged();
        };
        _pricePercent.Click += (_, _) => _priceSlider.Value = 100;
        ToolTip.SetTip(_priceSlider, "Raises or lowers every price on the card, and the total, by a percent of the price in your list (1% to 200%). Your list keeps its prices.");
        ToolTip.SetTip(_pricePercent, "Prices on the card as a percent of your list. Click to put it back to 100%.");
        var priceBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "Prices", VerticalAlignment = VerticalAlignment.Center }.Themed(TextBlock.ForegroundProperty, ThemeTokens.TextMuted), _priceSlider, _pricePercent } };
        DockPanel.SetDock(priceBar, Dock.Right);
        previewBar.Children.Add(priceBar);
        previewBar.Children.Add(new TextBlock { Text = "Your card", VerticalAlignment = VerticalAlignment.Center }.Themed(TextBlock.ForegroundProperty, ThemeTokens.TextMuted));
        var previewDock = new DockPanel();
        DockPanel.SetDock(previewBar, Dock.Top);
        previewDock.Children.Add(previewBar);
        previewDock.Children.Add(new ScrollViewer { Content = _preview, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var previewBox = Card(previewDock, 10);
        previewBox.Height = BodyHeight;
        _previewBox = previewBox;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape && _maximized) { ToggleMaximized(); e.Handled = true; } };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,460") };
        Grid.SetColumn(_sections, 0);
        Grid.SetColumn(previewBox, 2);
        body.Children.Add(_sections);
        body.Children.Add(previewBox);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var foot = Stack(2, _status);
        foot.Margin = new Thickness(2, 8, 0, 0);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(body);
        Content = root;

        _engine.Changed += () => Dispatcher.UIThread.Post(OnEngineChanged);
        _engine.CardChanged += () => Dispatcher.UIThread.Post(QueueRender);
        AttachedToVisualTree += (_, _) => { LoadFromSession(); OnEngineChanged(); QueueRender(); };
    }

    // ---------------------------------------------------------------- list

    Control BuildList()
    {
        _search.TextChanged += (_, _) => { if (_loading) return; S.Search = _search.Text ?? ""; RefreshList(); _engine.SettingsChanged(); };
        _show.SelectionChanged += (_, _) => { if (_loading || _show.SelectedItem is not string s) return; S.Show = s; RefreshList(); _engine.SettingsChanged(); };
        var checkAll = Button("Check all", () => _engine.SetIncluded(_shown, true));
        var uncheckAll = Button("Uncheck all", () => _engine.SetIncluded(_shown, false));
        ToolTip.SetTip(checkAll, "Puts every item shown in the list on the card");
        ToolTip.SetTip(uncheckAll, "Takes every item shown in the list off the card");
        var filterRow = new WrapPanel();
        var allButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { checkAll, uncheckAll } };
        filterRow.Children.Add(allButtons);
        filterRow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _search, _show } });

        _list.ItemsSource = _shown;
        _list.ItemTemplate = new FuncDataTemplate<SaleItem>((item, _) => Row());
        _list.Margin = new Thickness(8);   // on the list, not the ScrollViewer: its padding is left out when measuring, so full-width rows overflowed
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _empty.Themed(TextBlock.ForegroundProperty, ThemeTokens.TextMuted);
        var listDock = new DockPanel();
        var headers = SortHeaders();
        DockPanel.SetDock(headers, Dock.Top);
        listDock.Children.Add(headers);
        listDock.Children.Add(new Panel { Children = { _empty, scroll } });
        var listBox = Card(listDock, 0);

        var kinds = new Expander { Header = "Kinds of item on the card", Content = Stack(4,
            Muted("Include or remove whole groups; individual items can still be changed below."), _kinds) };
        _shopNote.TextWrapping = TextWrapping.Wrap;
        var head = Stack(8, filterRow, _tabs, _shopNote, kinds);
        head.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(listBox);
        // Rebuilding the list while an item's box has focus would throw away what is being typed: wait for focus to leave.
        _list.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => { if (_listDirty && !_list.IsKeyboardFocusWithin) RefreshList(); }, DispatcherPriority.Background);
        return dock;
    }

    /// <summary>One item: tick, icon, name and going rate, quantity, price, and a chevron for name, stats, slots and links.</summary>
    Control Row()
    {
        TextBox Box(string path, double width, string watermark, string tip)
        {
            var b = new TextBox { Width = width, Watermark = watermark, VerticalAlignment = VerticalAlignment.Center, [!TextBox.TextProperty] = new Binding(path) { Mode = BindingMode.TwoWay } };
            ToolTip.SetTip(b, tip);
            return b;
        }
        var include = new CheckBox { VerticalAlignment = VerticalAlignment.Center, [!ToggleButton.IsCheckedProperty] = new Binding(nameof(SaleItem.Include)) { Mode = BindingMode.TwoWay } };
        ToolTip.SetTip(include, "On the card");
        var icon = new Image { Width = 30, Height = 30, [!Image.SourceProperty] = new Binding(nameof(SaleItem.Icon)) };
        RenderOptions.SetBitmapInterpolationMode(icon, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        var tile = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.Parse("#E1DFD5")), Margin = new Thickness(8, 0, 10, 0), Child = icon };
        var name = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, [!TextBlock.TextProperty] = new Binding(nameof(SaleItem.Name)) }
            .Themed(TextBlock.ForegroundProperty, ThemeTokens.Text);
        var rate = new RatePicker();   // the going rate, as buttons that put a figure in the price box
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        rate.Margin = new Thickness(52, 2, 0, 0);
        var qty = Box(nameof(SaleItem.Qty), 56, "qty", "Quantity");
        var price = Box(nameof(SaleItem.Custom), 86, "price", "Your price. Leave it blank to use the going rate (or Offer when there is none), or click a figure under the name to put it here.");
        var more = new ToggleButton { Content = "▾", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(more, "Name, stats and slots");
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto") };
        foreach (var (c, i) in new Control[] { include, tile, words, qty, price, more }.Select((c, i) => (c, i))) { Grid.SetColumn(c, i); line.Children.Add(c); }
        price.Margin = new Thickness(6, 0, 0, 0); qty.Margin = new Thickness(8, 0, 0, 0);

        // details: name, stats, slots, read tooltip, look it up
        var details = new StackPanel { Spacing = 4, Margin = new Thickness(52, 8, 0, 4) };
        details.Children.Add(Muted("Name (edit if wrong)"));
        details.Children.Add(new TextBox { [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Name)) { Mode = BindingMode.TwoWay } });
        var equip = new StackPanel { Spacing = 4 };
        equip.Children.Add(Muted("Stats (include + / − and base rolls)"));
        equip.Children.Add(new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Stats)) { Mode = BindingMode.TwoWay } });
        var slotsLabel = Muted("Upgrade slots remaining");
        equip.Children.Add(slotsLabel);
        var slots = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left, [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Slots)) { Mode = BindingMode.TwoWay } };
        ToolTip.SetTip(slots, "The number beside Remaining Enhancements. Blank means unknown, not zero.");
        equip.Children.Add(slots);
        var readTip = new Button { Content = "Read tooltip (5s)", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        ToolTip.SetTip(readTip, "Click, then hover this copy of the item in the game. Keep the tooltip uncovered for five seconds.");
        readTip.Click += async (s, _) =>
        {
            if ((s as Control)?.DataContext is not SaleItem it) return;
            readTip.IsEnabled = false;
            try { await _engine.ReadEquipmentAsync(it); } finally { readTip.IsEnabled = true; }
        };
        equip.Children.Add(readTip);
        details.Children.Add(equip);
        var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        links.Children.Add(new TextBlock { Text = "Look it up:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4), FontSize = 11 }.Themed(TextBlock.ForegroundProperty, ThemeTokens.TextMuted));
        foreach (var (label, site, tip) in new[]
        {
            ("kerning.gg ↗", "kerning", "This item's page on kerning.gg (opens in your browser)"),
            ("meso.watch ↗", "meso", "This item's page on meso.watch (opens in your browser)"),
            ("maplestory-classic ↗", "classic", "This item's Free Market price page on maplestory-classic.com (opens in your browser)"),
            ("cozy.markets ↗", "cozy", "Cozy Market's item list, where you can search for this item (its pages can't be linked to directly)"),
            ("meowdb.com ↗", "nia", "NiaMeowDB's item database on meowdb.com, where you can search for this item (its pages can't be linked to directly)"),
        })
        {
            var b = new Button { Content = label, FontSize = 11, Margin = new Thickness(0, 0, 6, 4) };
            ToolTip.SetTip(b, tip);
            b.Click += (s, _) =>
            {
                if ((s as Control)?.DataContext is not SaleItem it) return;
                CatalogItem? cat = it.ItemId != 0 && _engine.Vision?.Catalog.TryGetValue(it.ItemId, out var c) == true ? c : null;
                OpenUrl(ItemLinks.For(site, it.ItemId, cat));
            };
            links.Children.Add(b);
        }
        details.Children.Add(links);
        details[!IsVisibleProperty] = more[!ToggleButton.IsCheckedProperty];
        var border = Card(new StackPanel { Children = { line, rate, details } }, 8);
        border.Margin = new Thickness(0, 0, 0, 4);
        border[!OpacityProperty] = new Binding(nameof(SaleItem.CardOpacity));
        border.DataContextChanged += (_, _) =>
        {
            var it = border.DataContext as SaleItem;
            equip.IsVisible = it is { IsEquipment: true } or { IsScroll: true };
            // scrolls have a success rate and an effect, not upgrade slots
            slotsLabel.IsVisible = slots.IsVisible = it is { IsEquipment: true };
        };
        return border;
    }


    bool _listDirty;

    // ---------------------------------------------------------------- buying: the whole item database

    const int BuyResultsMax = 120;
    readonly TextBox _buySearch = new() { Watermark = "Search every item by name", Width = 300 };
    readonly StackPanel _buyRows = new() { Spacing = 4 };
    readonly TextBlock _buyNote = Muted("");
    string _buyShown = "";   // what the rows show now, to rebuild only when it changes

    /// <summary>
    /// The Buying tab: search every item in the game's catalog and tick the ones you want. Each ticked item is a card
    /// tagged BUYING, with its own quantity and price (the going rate shows until you type one). What you're buying is
    /// listed first; the matches for the search follow.
    /// </summary>
    Control BuildBuying()
    {
        _buySearch.TextChanged += (_, _) => { if (_loading) return; S.BuySearch = _buySearch.Text ?? ""; RefreshBuying(force: true); _engine.SettingsChanged(); };
        var untick = Button("Untick all", () =>
        {
            foreach (var card in _engine.Items.Where(i => i.Tab == SaleCardEngine.BuyTab).ToList())
                if (_engine.Vision?.Catalog.TryGetValue(card.ItemId, out var cat) == true) _engine.SetBuying(cat, false);
            RefreshBuying(force: true);
        });
        ToolTip.SetTip(untick, "Takes every item off your Buying list");
        var head = Stack(6, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _buySearch, untick } },
            Muted("Tick what you want to buy. It goes on the card with a blue BUYING tag; set how many and what you'll pay. Use the Buying switch at the top to show or hide them all."),
            _buyNote);
        head.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        _buyRows.Margin = new Thickness(8);   // on the rows, not the ScrollViewer: its padding is left out when measuring, so a full-width row overflowed
        dock.Children.Add(Card(new ScrollViewer { Content = _buyRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 0));
        return dock;
    }

    void RefreshBuying(bool force = false)
    {
        if (_engine.Vision is not { } vision) return;
        var q = (S.BuySearch ?? "").Trim();
        var buying = _engine.Items.Where(i => i.Tab == SaleCardEngine.BuyTab).ToList();
        var have = buying.Select(i => i.ItemId).ToHashSet();
        var matches = q.Length == 0 ? new List<CatalogItem>()
            : vision.Catalog.Values.Where(c => c.name.Contains(q, StringComparison.OrdinalIgnoreCase) && !have.Contains(c.id))
                .OrderBy(c => c.name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(c => c.name, StringComparer.OrdinalIgnoreCase).ToList();
        var signature = string.Join(",", buying.Select(i => i.ItemId)) + "|" + q + "|" + string.Join(",", matches.Take(BuyResultsMax).Select(c => c.id));
        if (!force && (signature == _buyShown || _buyRows.IsKeyboardFocusWithin)) return;
        _buyShown = signature;
        _buyRows.Children.Clear();
        if (buying.Count > 0)
        {
            _buyRows.Children.Add(Title($"Buying ({buying.Count})"));
            foreach (var card in buying) _buyRows.Children.Add(BuyRow(card));
        }
        if (q.Length > 0)
        {
            var head = Title(matches.Count == 0 ? "No items match" : $"Matches ({matches.Count})");
            head.Margin = new Thickness(0, buying.Count > 0 ? 10 : 0, 0, 0);
            _buyRows.Children.Add(head);
            foreach (var cat in matches.Take(BuyResultsMax)) _buyRows.Children.Add(CatalogRow(cat));
        }
        _buyNote.Text = q.Length == 0 ? $"Type a name to search all {vision.Catalog.Count:N0} items."
            : matches.Count > BuyResultsMax ? $"Showing the first {BuyResultsMax} of {matches.Count} matches: type more of the name to narrow it down." : "";
        if (buying.Count == 0 && q.Length == 0) _buyRows.Children.Add(Muted("Nothing on your Buying list yet: search for an item above and tick it."));
    }

    Control ItemIcon(int id)
    {
        var icon = new Image { Width = 30, Height = 30, Source = _engine.IconOf(id) };
        RenderOptions.SetBitmapInterpolationMode(icon, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        return new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.Parse("#E1DFD5")), Margin = new Thickness(8, 0, 10, 0), Child = icon };
    }

    /// <summary>An item on your Buying list: untick to remove it; its quantity and price are what the card shows.</summary>
    Control BuyRow(SaleItem card)
    {
        var tick = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(tick, "Untick to take it off your Buying list");
        tick.IsCheckedChanged += (_, _) =>
        {
            if (tick.IsChecked == true || _engine.Vision?.Catalog.TryGetValue(card.ItemId, out var cat) != true) return;
            _engine.SetBuying(cat, false);
            Dispatcher.UIThread.Post(() => RefreshBuying(force: true));
        };
        var name = new TextBlock { Text = card.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }.Themed(TextBlock.ForegroundProperty, ThemeTokens.Text);
        var rate = new RatePicker { DataContext = card, Margin = new Thickness(52, 2, 0, 0) };
        var qty = new TextBox { Width = 56, Watermark = "qty", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Qty)) { Mode = BindingMode.TwoWay } };
        var price = new TextBox { Width = 86, Watermark = "price", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Custom)) { Mode = BindingMode.TwoWay } };
        ToolTip.SetTip(qty, "How many you want");
        ToolTip.SetTip(price, "What you'll pay (each, for more than one). Blank shows the going rate (or Offer when there is none); click a figure under the name to put it here.");
        var more = new ToggleButton { Content = "▾", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(more, "What you want: the stats and slots you're looking for");
        // a short summary of what you want, under the name
        var wants = new TextBlock { FontSize = 11, Margin = new Thickness(52, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }.Themed(TextBlock.ForegroundProperty, ThemeTokens.TextMuted);
        void Summary() { var w = card.WantedText; wants.Text = w.Length == 0 ? "" : w.Replace("Wants:\n", "Wants: ").Replace("\n", ", "); wants.IsVisible = w.Length > 0; }
        void Watch(object? s, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName is nameof(SaleItem.Stats) or nameof(SaleItem.Slots)) Summary(); }
        card.PropertyChanged += Watch;
        wants.DetachedFromVisualTree += (_, _) => card.PropertyChanged -= Watch;
        Summary();
        // The name takes the room left after the boxes; FitWidth below keeps a long one trimmed instead of widening the row.
        var line = new DockPanel { DataContext = card };
        var icon = ItemIcon(card.ItemId);
        foreach (var c in new Control[] { tick, icon }) { DockPanel.SetDock(c, Dock.Left); line.Children.Add(c); }
        foreach (var c in new Control[] { more, price, qty }) { DockPanel.SetDock(c, Dock.Right); line.Children.Add(c); }
        line.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name } });
        var details = WantedEditor(card);
        details[!IsVisibleProperty] = more[!ToggleButton.IsCheckedProperty];
        return new FitWidth { Child = Card(new StackPanel { Children = { line, rate, wants, details } }, 8) };
    }

    static readonly string[] WantedStats =
    {
        "STR", "DEX", "INT", "LUK", "Max HP", "Max MP", "Weapon Attack", "Magic Attack", "Weapon Def", "Magic Def",
        "Accuracy", "Avoidability", "Speed", "Jump", "Attack Speed", "Success rate",
    };
    static readonly string[] WantedHow = { "or more", "exactly", "or less" };

    /// <summary>
    /// What you want in an item you're buying: build stat lines (pick a stat, a value, "or more" / "exactly" / "or less",
    /// Add), edit them freely, and for equipment the upgrade slots left. The card shows them under "Wants:".
    /// </summary>
    Control WantedEditor(SaleItem card)
    {
        var stat = new ComboBox { ItemsSource = WantedStats, SelectedIndex = 6, MinWidth = 150 };
        var value = new TextBox { Width = 70, Watermark = "+5" };
        var how = new ComboBox { ItemsSource = WantedHow, SelectedIndex = 0, MinWidth = 100 };
        var stats = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, Watermark = "One per line, e.g.\nWeapon Attack: +5 or more\nClean (no scrolls used)",
            DataContext = card, [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Stats)) { Mode = BindingMode.TwoWay },
        };
        var add = Button("Add", () =>
        {
            var label = stat.SelectedItem as string ?? "";
            var v = (value.Text ?? "").Trim();
            if (label.Length == 0 || v.Length == 0) return;
            bool number = System.Text.RegularExpressions.Regex.IsMatch(v, @"^[+-]?\d+%?$");
            // numbers read like the game's tooltips: "+5" (a rate as "60%"), with how they compare; words ("Fast") as they are
            if (number && label == "Success rate") v = v.TrimStart('+').TrimEnd('%') + "%";
            else if (number && !v.StartsWith('+') && !v.StartsWith('-')) v = "+" + v;
            var line = number ? $"{label}: {v} {how.SelectedItem}" : $"{label}: {v}";
            card.Stats = string.IsNullOrWhiteSpace(card.Stats) ? line : card.Stats.TrimEnd() + "\n" + line;
            value.Text = "";
        });
        ToolTip.SetTip(add, "Adds this line to what you want");
        var builder = new WrapPanel { Children = { stat, value, how, add } };
        foreach (var c in builder.Children) c.Margin = new Thickness(0, 0, 6, 4);
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(52, 8, 0, 4) };
        panel.Children.Add(Muted("What you want (shown on the card under \"Wants:\")"));
        panel.Children.Add(builder);
        panel.Children.Add(stats);
        if (card.IsEquipment)
        {
            var slots = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Watermark = "any", DataContext = card,
                [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Slots)) { Mode = BindingMode.TwoWay } };
            ToolTip.SetTip(slots, "The fewest upgrade slots left you'd take (7 shows as \"7 or more\"). Blank: any.");
            panel.Children.Add(Muted("Upgrade slots remaining, at least"));
            panel.Children.Add(slots);
        }
        var clear = Button("Clear", () => { card.Stats = ""; card.Slots = ""; });
        ToolTip.SetTip(clear, "Removes everything you said you want");
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(clear);
        return panel;
    }

    /// <summary>A catalog item found by the search: tick it to add it to your Buying list.</summary>
    Control CatalogRow(CatalogItem cat)
    {
        var tick = new CheckBox { IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(tick, "Tick to add it to your Buying list (it goes on the card with a BUYING tag)");
        tick.IsCheckedChanged += (_, _) =>
        {
            if (tick.IsChecked != true) return;
            _engine.SetBuying(cat, true);
            Dispatcher.UIThread.Post(() => RefreshBuying(force: true));
        };
        var name = new TextBlock { Text = cat.name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }.Themed(TextBlock.ForegroundProperty, ThemeTokens.Text);
        var kind = new TextBlock { Text = Categories.Of(cat.id), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }.Themed(TextBlock.ForegroundProperty, ThemeTokens.TextMuted);
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        foreach (var (c, i) in new Control[] { tick, ItemIcon(cat.id), name, kind }.Select((c, i) => (c, i))) { Grid.SetColumn(c, i); line.Children.Add(c); }
        return Card(line, 6);
    }

    // ---------------------------------------------------------------- sorting the list

    readonly List<(Button button, string key, string label)> _sortButtons = new();

    /// <summary>
    /// Column headers over the list, lined up with each row: ✓ (on the card), Name, Qty, Price. A click sorts by that
    /// column (▲), the next click reverses it (▼), the third goes back to the order the items were found in.
    /// </summary>
    Control SortHeaders()
    {
        Button Head(string key, string label, string tip, HorizontalAlignment align)
        {
            var b = new Button { Content = label, Padding = new Thickness(4, 1), FontSize = 12, HorizontalAlignment = align, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) =>
            {
                if (S.ListSort != key) { S.ListSort = key; S.ListSortDown = false; }
                else if (!S.ListSortDown) S.ListSortDown = true;
                else { S.ListSort = ""; S.ListSortDown = false; }
                ShowSort(); RefreshList(); _engine.SettingsChanged();
            };
            _sortButtons.Add((b, key, label));
            return b;
        }
        // same columns as a row: tick, icon, name, qty (70 + 8), price (96 + 6), chevron
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("30,52,*,64,92,28"), Margin = new Thickness(18, 8, 10, 0) };
        var cells = new (Control c, int col, int span)[]
        {
            (Head("on", "✓", "Sort by whether the item is on the card", HorizontalAlignment.Left), 0, 1),
            (Head("name", "Name", "Sort by name", HorizontalAlignment.Left), 1, 2),
            (Head("qty", "Qty", "Sort by quantity", HorizontalAlignment.Left), 3, 1),
            (Head("price", "Price", "Sort by price: the one you typed, else the going rate; Offer last", HorizontalAlignment.Left), 4, 1),
        };
        foreach (var (c, col, span) in cells) { Grid.SetColumn(c, col); Grid.SetColumnSpan(c, span); grid.Children.Add(c); }
        foreach (var (b, _, _) in _sortButtons) b.Themed(TemplatedControl.ForegroundProperty, ThemeTokens.TextMuted);
        ShowSort();
        return grid;
    }

    void ShowSort()
    {
        foreach (var (b, key, label) in _sortButtons)
        {
            bool on = S.ListSort == key;
            b.Content = on ? $"{label} {(S.ListSortDown ? "▼" : "▲")}" : label;
            b.FontWeight = on ? FontWeight.Bold : FontWeight.Normal;
        }
    }

    /// <summary>The list in the chosen column order; ties (and no sort) keep the order the items were found in.</summary>
    IEnumerable<SaleItem> Sorted(IEnumerable<SaleItem> items)
    {
        var list = items.ToList();
        static long Price(SaleItem i) => (!string.IsNullOrWhiteSpace(i.Custom) ? Mesos.Parse(i.Custom) : i.RateValue) ?? -1;
        IOrderedEnumerable<SaleItem>? o = S.ListSort switch
        {
            "on" => S.ListSortDown ? list.OrderBy(i => i.Include) : list.OrderByDescending(i => i.Include),
            "name" => S.ListSortDown ? list.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase) : list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "qty" => S.ListSortDown ? list.OrderByDescending(i => i.Pieces ?? 0) : list.OrderBy(i => i.Pieces ?? 0),
            // Offer (no price) always last, both ways
            "price" => S.ListSortDown ? list.OrderBy(i => Price(i) < 0).ThenByDescending(Price) : list.OrderBy(i => Price(i) < 0).ThenBy(Price),
            _ => null,
        };
        return o ?? (IEnumerable<SaleItem>)list;
    }

    bool Shows(SaleItem it)
    {
        var q = (S.Search ?? "").Trim();
        if (q.Length > 0 && !it.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return false;
        if (!SaleCardEngine.InTab(it, S.Tab)) return false;
        return S.Show switch
        {
            "" or ShowAll => true,
            ShowOn => it.Include,
            ShowOff => !it.Include,
            SaleCardEngine.PlaceStorage => it.Tab == SaleCardEngine.StorageTab,
            SaleCardEngine.PlaceBag => SaleCardEngine.PlaceOf(it) == SaleCardEngine.PlaceBag,
            SaleCardEngine.PlaceShop => it.Tab == SaleCardEngine.ShopTab,
            var cat => it.Category == cat,
        };
    }

    void RefreshList()
    {
        _shopNote.IsVisible = S.Tab == SaleCardEngine.ShopTab || S.Show == SaleCardEngine.PlaceShop;
        var want = Sorted(_engine.Items.Where(Shows)).ToList();
        if (!want.SequenceEqual(_shown))
        {
            if (_list.IsKeyboardFocusWithin) { _listDirty = true; return; }
            _shown.Clear();
            foreach (var i in want) _shown.Add(i);
        }
        _listDirty = false;
        _empty.IsVisible = _shown.Count == 0;
        int inTab = _engine.Items.Count(i => SaleCardEngine.InTab(i, S.Tab));
        _empty.Text = inTab > 0 ? "No items here match the search or the Show filter."
            : S.Tab.Length == 0 ? "Open your inventory, storage or your own Free Market shop in game"
            : S.Tab == SaleCardEngine.ShopTab ? "Open your own Free Market shop in game.\nVisible, recognized listings are saved here; scroll to read more."
            : S.Tab == SaleCardEngine.StorageTab ? "Nothing from storage yet.\nTalk to a storage keeper NPC in town and open your storage in game."
            : $"Open the {S.Tab} tab in game";
    }

    void RebuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var name in new[] { "" }.Concat(Vision.TabNames).Append(SaleCardEngine.StorageTab).Append(SaleCardEngine.ShopTab))
        {
            int count = _engine.Items.Count(i => SaleCardEngine.InTab(i, name));
            var b = new ToggleButton { Content = $"{(name.Length == 0 ? "All" : name)} ({count})", IsChecked = S.Tab == name, Margin = new Thickness(0, 0, 4, 4) };
            if (count == 0 && name.Length > 0)
            {
                b.Opacity = 0.6;
                ToolTip.SetTip(b, name == SaleCardEngine.ShopTab ? "Open your own Free Market shop; scroll its list to capture more listings" : name == SaleCardEngine.StorageTab ? "Talk to a storage keeper NPC in town and open your storage in game" : $"Open the {name} tab in game");
            }
            b.Click += (_, _) => { S.Tab = name; RebuildTabs(); RefreshList(); _engine.SettingsChanged(); };
            _tabs.Children.Add(b);
        }
    }

    /// <summary>One checkbox per kind of item present (and inventory / storage): untick to take that whole group off the card.</summary>
    void RebuildFilters()
    {
        var items = _engine.Items.Where(i => i.Tab != SaleCardEngine.BuyTab).ToList();   // selling items: what you buy has its own tab
        var groups = Categories.Order.Where(c => items.Any(i => i.Category == c)).ToList();
        _kinds.Children.Clear();
        void Group(string label, List<SaleItem> members, Action<bool> set, bool place)
        {
            int on = members.Count(i => i.Include);
            var box = new CheckBox { Content = $"{label} ({on}/{members.Count})", IsChecked = on == members.Count ? true : on == 0 ? false : null, Margin = new Thickness(0, 0, 16, 4) };
            if (place) box.FontWeight = FontWeight.SemiBold;
            ToolTip.SetTip(box, $"Tick to put all {label} on the card, untick to remove them all");
            box.Click += (_, _) => set(box.IsChecked == true);
            _kinds.Children.Add(box);
        }
        foreach (var c in groups)
        {
            var members = items.Where(i => i.Category == c).ToList();
            Group(c, members, v => _engine.SetGroup(c, v, members), false);
        }
        var places = new[] { SaleCardEngine.PlaceBag, SaleCardEngine.PlaceStorage, SaleCardEngine.PlaceShop };
        foreach (var place in places)
        {
            var members = items.Where(i => SaleCardEngine.PlaceOf(i) == place).ToList();
            if (members.Count > 0) Group(place, members, v => _engine.SetGroup(place, v, members), true);
        }
        if (groups.Count == 0) _kinds.Children.Add(Muted("read a tab to see the kinds of item"));

        var options = new List<string> { ShowAll, ShowOn, ShowOff };
        options.AddRange(places);
        options.AddRange(groups);
        if (!options.SequenceEqual(_show.Items.Cast<string>()))
        {
            _loading = true;
            _show.Items.Clear();
            foreach (var o in options) _show.Items.Add(o);
            _show.SelectedItem = options.Contains(S.Show) ? S.Show : ShowAll;
            _loading = false;
        }
    }

    // ---------------------------------------------------------------- panels

    static TextBlock Head(string text) { var t = Title(text); t.Margin = new Thickness(0, 10, 0, 4); return t; }

    TextBox Field(TextBox box, Action<string> set, string tip = "")
    {
        box.TextChanged += (_, _) => { if (_loading) return; set(box.Text ?? ""); _engine.SettingsChanged(); UpdateHeader(); };
        if (tip.Length > 0) ToolTip.SetTip(box, tip);
        return box;
    }

    CheckBox Check(string text, Func<bool> get, Action<bool> set, string tip = "")
    {
        var c = new CheckBox { Content = text, Margin = new Thickness(0, 0, 0, 4) };
        c.Tag = get;
        c.IsCheckedChanged += (_, _) => { if (_loading) return; set(c.IsChecked == true); _engine.SettingsChanged(); };
        if (tip.Length > 0) ToolTip.SetTip(c, tip);
        _checks.Add((c, get));
        return c;
    }
    readonly List<(CheckBox box, Func<bool> get)> _checks = new();
    readonly List<Action> _loaders = new();

    ComboBox Choice(IEnumerable<string> items, Func<string> get, Action<string> set, string tip = "")
    {
        var c = new ComboBox { ItemsSource = items.ToList(), MinWidth = 160 };
        c.SelectionChanged += (_, _) => { if (_loading || c.SelectedItem is not string s) return; set(s); _engine.SettingsChanged(rates: true); };
        if (tip.Length > 0) ToolTip.SetTip(c, tip);
        _loaders.Add(() => { var list = (List<string>)c.ItemsSource!; c.SelectedIndex = Math.Max(0, list.IndexOf(get())); });
        return c;
    }

    static Grid Two(Control a, Control b)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        Grid.SetColumn(a, 0); Grid.SetColumn(b, 2);
        g.Children.Add(a); g.Children.Add(b);
        return g;
    }

    Control BuildDesign()
    {
        var seller = Stack(4, Muted("IGN"), Field(_seller, v => S.Seller = v), Muted("Channel"), Field(_channel, v => S.Channel = v));
        var server = Stack(4, Muted("Server"), Field(_server, v => S.Server = v), Muted("Tagline"),
            Field(_tagline, v => S.Tagline = v, "A line shown under your server on the card. Leave it empty to show nothing."));
        var manual = Button("Manual read", async () => await _engine.ManualReadAsync());
        ToolTip.SetTip(manual, "Reads your character name from the status bar, the map name from the minimap, and the channel if the Change Channel window is open");
        var portrait = Button("Take character portrait", async () => await SnipAsync());
        ToolTip.SetTip(portrait, "Takes the newest game picture and lets you draw a box around your character.");

        var look = Two(Stack(4, Muted("Colours"), Choice(CardRenderer.Themes.Select(t => t.Name), () => S.Theme, v => S.Theme = v, "Colour scheme of the card")),
                       Stack(4, Muted("Order"), Choice(SaleCardEngine.Orders, () => S.Order, v => S.Order = v,
                           "The order of the tiles. By type keeps each kind together (weapons, armor, accessories, potions, scrolls, gems and ores…), related items side by side.")));

        var hideOffer = Check("Hide \"Offer\"", () => S.HideOffer, v => S.HideOffer = v, "Items with no price show nothing instead of Offer, while the other prices still show.");
        var show = Stack(0,
            Check("Character picture", () => !S.NoAvatar, v => S.NoAvatar = !v, "Shows your character picture. Take character portrait lets you draw one by hand."),
            Check("Minimap", () => !S.NoMap, v => S.NoMap = !v, "The minimap picture, with the dot for where you stand. Read while the minimap is open at full or middle size."),
            Check("Total", () => !S.NoTotal, v => S.NoTotal = !v, "The total for everything on the card (price each × quantity). Items priced Offer are counted separately."),
            Check("\"ea\" after prices of stacks", () => !S.NoEach, v => S.NoEach = !v,
                "Adds \"ea\" after the price on cards that hold more than one (×2 chairs show 9999 ea), so buyers know it's the price of each. Throwing stars and bullets: the price of each set."),
            Check("OBO after prices", () => S.Obo, v => S.Obo = v, "Adds OBO (or best offer) after every price"),
            Check("Equipment stats", () => !S.NoStats, v => S.NoStats = !v, "Stats and remaining upgrade slots under each piece of equipment"),
            Check("Compact tiles", () => S.CompactTiles, v => S.CompactTiles = v, "Smaller tiles: the icon on the left, the name and price beside it"),
            Check("Pack tiles tightly", () => !S.NoPack, v => S.NoPack = !v,
                "Each tile goes under the shortest column at its own height, so tall equipment and short stacks fit together with no gaps and no stretched tiles. The order then runs roughly left to right. Untick for even rows."),
            hideOffer,
            Check("Mark items as Sale or Buying", () => !S.NoTradeTags, v => S.NoTradeTags = !v,
                "Puts a pink SALE tag on the items you sell (the Selling tab) and a blue BUYING tag on the ones you want (the Buying tab)."),
            Check("Mark items in my Free Market shop", () => !S.NoShopMarker, v => S.NoShopMarker = !v,
                "Puts a green IN SHOP tag on items listed in your Free Market shop right now, and a line under the items: those can be bought in your shop; for everything else, DM you on Discord or whisper your IGN. Shows when the card has items from your shop."),
            Muted("Discord name (optional, for the \"DM me on Discord\" line)"),
            Field(_discord, v => S.Discord = v, "Shown as \"DM <name> on Discord\". Leave empty to just say \"DM me on Discord\"."));

        var zone = Choice(CardRenderer.Zones.Select(z => z.Label), () => S.Zone, v => S.Zone = v, "Time zone for the second time. Daylight time is followed (EST shows as EDT in summer).");
        zone.MinWidth = 90;
        var footer = Stack(0,
            Check("Time (UTC)", () => !S.NoTime, v => S.NoTime = !v, "When the card was made (UTC), at the bottom"),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Check("Second time zone", () => S.LocalTime, v => S.LocalTime = v, "Shows the time in the zone you pick beside the UTC time"), zone } });

        // Map: trim its plain sides, crop it by dragging on it, and choose how large it is on the card.
        var mapTrim = Check("Trim plain sides off the map", () => S.MapTrim, v => S.MapTrim = v,
            "Cuts plain bands (sky, empty bars) off the left and right of the minimap picture before it goes on the card.");
        _cropper = new MapCropper(_engine);
        var sizeLabel = Muted("");
        var size = new Slider { Minimum = 40, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        void ShowSize() => sizeLabel.Text = $"Map size on the card: {(int)size.Value}%";
        size.ValueChanged += (_, _) => { ShowSize(); if (_loading) return; S.MapScale = (int)size.Value; _engine.SettingsChanged(); };
        _loaders.Add(() => { size.Value = Math.Clamp(S.MapScale, 40, 100); ShowSize(); });
        ToolTip.SetTip(size, "How much of its room (two tiles wide) the map takes on the card");
        var marker = Check("Seller location", () => !S.NoSellerMarker, v => S.NoSellerMarker = !v,
            "Circles your yellow dot on the card's map, with an arrow and your IGN beside it, so buyers can find you. Shows when your dot is in the part of the map on the card.");
        var colours = Stack(4,
            ColourRow("Circle and arrow", () => S.MarkerRing, v => S.MarkerRing = v),
            ColourRow("Name tag", () => S.MarkerTagColour, v => S.MarkerTagColour = v),
            ColourRow("Name text", () => S.MarkerText, v => S.MarkerText = v));
        colours.Margin = new Thickness(25, 0, 0, 4);
        var map = Stack(6, marker, colours, mapTrim,
            Muted("Drag a box on the map to keep only that part on the card. Drag your name tag to move it; the arrow stays on your dot."),
            _cropper, sizeLabel, size);

        return Card(Stack(6,
            Head("Seller"), Two(seller, server),
            Muted("Location"), Field(_location, v => S.Location = v),
            Muted("Card title"), Field(_title, v => S.Title = v),
            Wrap(manual, portrait),
            Head("Look"), look,
            Head("Show on the card"), show,
            Head("Map"), map,
            Head("Footer"), footer));
    }

    Control BuildSettings()
    {
        var watch = Check("Watch the game", () => !S.MonitorOff, v => _engine.SetMonitor(v),
            "Reads the game picture about once a second: inventory tabs, storage, hovered tooltips, player shops, minimap, IGN and channel. The list stays as it is while this is off.");
        var tabs = Check("Read price pages in my own browser", () => S.BrowserTabs, v => _engine.SetBrowserTabs(v),
            "Off unless you turn it on. When on, MapleLite looks for a MesoWatch, Cozy Market, NiaMeowDB or maplestory-classic.com tab showing in Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Safari or Arc and reads its prices from a picture of that window. It sends the sites nothing and never touches the browser.");
        var browserNote = Muted("Off by default: MapleLite only looks at your browser windows when this is on, and only at price-site tabs. A built-in price browser is planned.");
        _browserStatus.Margin = new Thickness(25, 0, 0, 0);

        var fmNote = Muted("Monitors the Free Market shops you visit in game and keeps the asking prices it sees.");
        fmNote.Margin = new Thickness(25, -2, 0, 8);
        var sources = Stack(0,
            Check("In-Game Free Market Shops", () => !S.NoFm, v => { S.NoFm = !v; _engine.SettingsChanged(rates: true); },
                "The asking prices MapleLite has seen in player shops you opened in the game"),
            fmNote);
        foreach (var (site, label, tip) in new[]
        {
            (PriceSites.Meso, "meso.watch", "The chosen figure read off an item's panel on MesoWatch, or the Typical price in its list"),
            (PriceSites.Cozy, "cozy.markets", "The Usual price on Cozy Market (list or item page), or its Last sold when the figure is Last sale"),
            (PriceSites.Classic, "maplestory-classic", "The median sale price on maplestory-classic.com (one figure, used whatever figure is picked)"),
            (PriceSites.Nia, "niadb", "The NiaMeowDB median price from an item's Free Market section, or its Sold-for figure when the figure is Last 10 median"),
        })
            sources.Children.Add(Check(label, () => S.SourceOn(site), v => { S.SetSource(site, v); _engine.SettingsChanged(rates: true); }, tip));
        var figure = Choice(SaleCardEngine.SiteModes, () => SaleCardEngine.SiteModes[S.SiteFigure], v => { S.Figure = Array.IndexOf(SaleCardEngine.SiteModes, v); S.FigureV = 2; },
            "Which price to take from every ticked site. Usual / typical: all sites have it. Last sale: MesoWatch and Cozy Market. Last 3 avg: MesoWatch only. Last 10 median: MesoWatch and NiaMeowDB.");

        var refresh = Button("Refresh now", async () => await _engine.RefreshNowAsync());
        ToolTip.SetTip(refresh, "Reads the newest game picture again right away: inventory, storage, location and character picture.");
        var clear = Button("Clear list", () => _engine.Clear());
        ToolTip.SetTip(clear, "Empties the list. Whatever is open in the game is read in again straight away.");

        var import = Button("Import from Sale Card Generator…", async () => await ImportAsync());
        ToolTip.SetTip(import, "Copies the app's list, prices, FM shop prices, site prices and equipment stats from its Data folder. The app's files are not changed; what MapleLite had is backed up first.");
        var openData = Button("Open data folder", () => OpenFolder(_engine.DataFolder));
        var openChecks = Button("Open hover checks", () => OpenFolder(_engine.DiagnosticsFolder));
        ToolTip.SetTip(openChecks, "When a hovered tooltip can't be read three times in a row, the game picture and the reason are saved here, so the miss can be looked into.");

        var resetList = Button("Reset settings and list…", async () =>
        {
            if (await Confirm("Reset the Shop Card to how it starts?", ClearsAlways, new[] { "Your saved prices, the FM shop prices seen, and the price site figures" }, "Reset"))
                _engine.Reset(prices: false);
            LoadFromSession();
        });
        var resetAll = Button("Reset everything…", async () =>
        {
            if (await Confirm("Reset everything, including every price record?", ClearsAlways.Concat(new[]
                {
                    "The prices you saved for items", "Every FM shop price seen", "Every figure read from meso.watch, cozy.markets, niadb and maplestory-classic.com",
                }).ToArray(), Array.Empty<string>(), "Reset everything"))
                _engine.Reset(prices: true);
            LoadFromSession();
        });

        return Card(Stack(6,
            Head("Watching"), watch, Muted("The game window is the one picked on MapleLite's Game page."),
            tabs, browserNote, _browserStatus,
            Head("Going rate"), Muted("When you have not typed a price, a card uses the average of the ticked sources."), sources,
            Muted("Price to take from the sites"), figure,
            Head("List"), Wrap(refresh, clear, ShopBack()),
            Head("Data"), Muted("Saved in MapleLite's data folder (plugins/salecard), in the same files as the Sale Card Generator app."), Wrap(import, openData, openChecks),
            Head("Reset"), Muted("Both ask first and keep a copy of what they clear in the Backups folder."), Wrap(resetList, resetAll)));
    }

    static readonly string[] Swatches = { "#E5332A", "#FF8A00", "#FFD400", "#2E9B4F", "#2F7DF6", "#8E44AD", "#FF4FA3", "#FFFFFF", "#111111" };

    /// <summary>A colour choice: a label, quick swatches, and a box for any "#RRGGBB". The chosen swatch is outlined.</summary>
    Control ColourRow(string label, Func<string> get, Action<string> set)
    {
        var buttons = new List<(Button b, string hex)>();
        var hexBox = new TextBox { Width = 86, Watermark = "#RRGGBB", VerticalAlignment = VerticalAlignment.Center };
        void Show()
        {
            var now = get() ?? "";
            foreach (var (b, hex) in buttons)
            {
                bool on = string.Equals(hex, now, StringComparison.OrdinalIgnoreCase);
                b.BorderThickness = new Thickness(on ? 2.5 : 1);
                b.BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromArgb(120, 128, 128, 128));
            }
            if (!hexBox.IsFocused) hexBox.Text = now;
        }
        void Choose(string hex)
        {
            if (_loading) return;
            set(hex.ToUpperInvariant());
            Show();
            _engine.SettingsChanged();
            _cropper?.Refresh();
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var name = Muted(label);
        name.Width = 110; name.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(name);
        foreach (var hex in Swatches)
        {
            var b = new Button { Width = 22, Height = 22, Padding = new Thickness(0), CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.Parse(hex)) };
            ToolTip.SetTip(b, hex);
            b.Click += (_, _) => Choose(hex);
            buttons.Add((b, hex));
            row.Children.Add(b);
        }
        hexBox.TextChanged += (_, _) =>
        {
            var t = (hexBox.Text ?? "").Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (hexBox.IsFocused && System.Text.RegularExpressions.Regex.IsMatch(t, "^#[0-9A-Fa-f]{6}$")) Choose(t);
        };
        ToolTip.SetTip(hexBox, "Any colour as #RRGGBB");
        row.Children.Add(hexBox);
        _loaders.Add(Show);
        return row;
    }

    Button ShopBack()
    {
        var b = Button("Move shop listings back", () => _engine.ShopClosedByHand());
        ToolTip.SetTip(b, "Your Free Market shop is closed: moves its unsold listings (the My FM shop cards) back to their inventory tabs, keeping prices and stats. This happens by itself when MapleLite sees your shop close.");
        return b;
    }

    static readonly string[] ClearsAlways =
    {
        "The item list, with the prices typed on cards and the stats you entered",
        "Every setting, the card details (name, server, tagline, title) and the card look",
        "The character picture and the map",
        "The remembered equipment stats and the not-for-sale list",
    };

    // ---------------------------------------------------------------- state

    void LoadFromSession()
    {
        _loading = true;
        try
        {
            _search.Text = S.Search;
            _prices.IsChecked = S.PricesShown;
            _priceSlider.Value = Math.Clamp(S.PricePercent, 1, 200); _pricePercent.Content = Math.Clamp(S.PricePercent, 1, 200) + "%";
            _seller.Text = S.Seller; _channel.Text = S.Channel; _server.Text = S.Server; _tagline.Text = S.Tagline ?? ""; _discord.Text = S.Discord ?? "";
            _location.Text = S.Location; _title.Text = S.Title;
            foreach (var (box, get) in _checks) box.IsChecked = get();
            foreach (var load in _loaders) load();
            ShowSort();
            _buySearch.Text = S.BuySearch ?? "";
            _sellingOn.IsChecked = !S.HideSelling; _buyingOn.IsChecked = !S.HideBuying;
        }
        finally { _loading = false; }
    }

    /// <summary>The monitor fills IGN, channel and location: show them unless the user is typing in that box.</summary>
    void SyncFields()
    {
        _loading = true;
        try
        {
            void Sync(TextBox box, string value) { if (!box.IsFocused && box.Text != value) box.Text = value; }
            Sync(_seller, S.Seller); Sync(_channel, S.Channel); Sync(_location, S.Location);
            foreach (var (box, get) in _checks) if (box.IsChecked != get()) box.IsChecked = get();
        }
        finally { _loading = false; }
    }

    void UpdateHeader()
    {
        _sellerLine.Text = S.Seller;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(S.Server)) parts.Add(S.Server);
        if (!string.IsNullOrWhiteSpace(S.Channel)) parts.Add("Ch " + S.Channel);
        if (!string.IsNullOrWhiteSpace(S.Location)) parts.Add(S.Location);
        _gameLine.Text = string.Join("  ·  ", parts);
    }

    void OnEngineChanged()
    {
        _status.Text = _engine.Status;
        _browserStatus.Text = S.BrowserTabs ? _engine.BrowserStatus : "";
        SyncFields();
        UpdateHeader();
        RebuildTabs();
        RebuildFilters();
        RefreshList();
        RefreshBuying();
    }

    // ---------------------------------------------------------------- card

    void QueueRender() { _render.Stop(); _render.Start(); }

    MapCropper? _cropper;
    bool _maximized;
    Button _maximize = null!;
    Border _previewBox = null!;

    /// <summary>
    /// Maximize: the card takes the whole width of the page in place of the Items / Design / Settings panel, enlarged
    /// to fit. Reduce (or Esc) puts the panel back. The card keeps following every change either way.
    /// </summary>
    void ToggleMaximized()
    {
        _maximized = !_maximized;
        _sections.IsVisible = !_maximized;
        Grid.SetColumn(_previewBox, _maximized ? 0 : 2);
        Grid.SetColumnSpan(_previewBox, _maximized ? 3 : 1);
        _previewBox.Height = _maximized ? double.NaN : BodyHeight;
        _preview.StretchDirection = _maximized ? StretchDirection.Both : StretchDirection.DownOnly;
        _maximize.Content = _maximized ? "⤡  Reduce" : "⛶  Maximize";
        ToolTip.SetTip(_maximize, _maximized ? "Back to the lists beside the card (Esc)" : "Shows the card across the whole page, as large as it fits. Press Reduce (or Esc) to bring the lists back.");
        if (_maximized) { Focusable = true; Focus(); }
    }

    void RenderCard()
    {
        try
        {
            var (items, options) = _engine.Card();
            _card = CardRenderer.Render(items, options);
            _preview.Source = _card.Value.bitmap;
            _cropper?.Refresh();   // the map may have refreshed, been trimmed or cropped
        }
        catch (Exception e) { _ctx.Log("card render failed: " + e); _status.Text = "Couldn't draw the card: " + e.Message; }
    }

    async Task CopyAsync()
    {
        RenderCard();
        if (_card == null) return;
        try
        {
            if (await _ctx.CopyImageAsync(_card.Value.frame)) _engine.Report("Image copied. Paste it into Discord with Ctrl+V." + _engine.MissingNote());
            else _engine.Report("Copying pictures isn't supported on this system yet: use Save PNG… instead.");
        }
        catch (Exception e) { _engine.Report("Copy failed: " + e.Message); }
    }

    async Task SaveAsync()
    {
        RenderCard();
        if (_card == null || TopLevel.GetTopLevel(this) is not { } top) return;
        var start = await top.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures);
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the sale card", SuggestedFileName = $"SaleCard-{DateTime.Now:yyyyMMdd-HHmm}.png", DefaultExtension = "png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } } }, SuggestedStartLocation = start,
        });
        if (file == null) return;
        await using (var s = await file.OpenWriteAsync())
        {
            var png = FrameFiles.EncodePng(_card.Value.frame);
            await s.WriteAsync(png);
        }
        _engine.Report("Saved " + (file.TryGetLocalPath() ?? file.Name) + _engine.MissingNote());
    }

    async Task ImportAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        IStorageFolder? start = null;
        if (SaleCardEngine.LikelyAppDataFolders().FirstOrDefault() is { } guess) start = await top.StorageProvider.TryGetFolderFromPathAsync(guess);
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick the Sale Card Generator's Data folder (beside MapleStorySaleCardGenerator.csproj, or beside its .exe)",
            SuggestedStartLocation = start, AllowMultiple = false,
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;
        if (!await Confirm("Import the Sale Card Generator's data?",
                new[] { "What the Shop Card plugin has now (a copy is kept in its Backups folder)" },
                new[] { "The Sale Card Generator app's own files: they are only copied" }, "Import")) return;
        try { _engine.Import(folder); LoadFromSession(); }
        catch (Exception e) { _engine.Report("Import failed: " + e.Message); }
    }

    async Task SnipAsync()
    {
        var frame = _engine.FrameForSnip();
        if (frame == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var px = await SnipWindow.SelectAsync(owner, frame);
        if (px != null) { _engine.SetManualAvatar(px); LoadFromSession(); }
    }

    async Task<bool> Confirm(string question, string[] clears, string[] keeps, string go)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await ConfirmWindow.AskAsync(owner, question, clears, keeps, go);
    }
}

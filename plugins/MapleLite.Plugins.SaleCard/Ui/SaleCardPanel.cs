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
/// The Shop Card page: available and selected items for Selling and Buying, the card preview, and the Card Design and
/// Settings panels, which take the list's place while the preview stays in view.
/// </summary>
internal sealed class SaleCardPanel : UserControl
{
    const double BodyHeight = 500;

    readonly SaleCardEngine _engine;
    readonly IPluginContext _ctx;
    Session S => _engine.S;
    readonly ObservableCollection<SaleItem> _shown = new();
    readonly ItemsControl _list = new();
    readonly TextBlock _empty = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(30), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _search = new() { Watermark = "Search items…", HorizontalAlignment = HorizontalAlignment.Stretch };
    static readonly string[] ItemSources = ["All sources", "Inventory", "Equipped", "Storage", "My FM shop", "Manual"];
    readonly ComboBox _source = new() { MinWidth = 135, ItemsSource = ItemSources, SelectedIndex = 0 };
    readonly ComboBox _type = new() { MinWidth = 135 };
    readonly ObservableCollection<SaleItem> _selected = new();
    readonly ItemsControl _selectedList = new();
    readonly Button _missingStats = new() { HorizontalAlignment = HorizontalAlignment.Left };
    bool _missingOnly;
    bool _reviewPrices;
    readonly Button _availablePrices = new();
    readonly Button _selectedPrices = new();
    readonly TextBlock _selectedTitle = PaneTitle("On your card · 0");
    readonly TextBlock _selectedEmpty = Muted("Add items from the list on the left, or search the item database with + Add item.");
    readonly TextBlock _selectionHidden = Muted("Selling is hidden from the exported card. Turn on Selling in the preview to show these items.");
    readonly StackPanel _kindPrices = new() { Spacing = 6 };
    string _kindPricesFor = "\0";   // the kinds the sliders were last built for
    readonly Image _preview = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _status = Muted(""), _browserStatus = Muted(""), _gameLine = Muted(""), _sellerLine = Text("", ThemeTokens.Text, 16, FontWeight.SemiBold);
    readonly TabControl _sections = new();
    readonly ToggleSwitch _prices = new() { OnContent = "Prices shown", OffContent = "Prices hidden" };
    readonly ToggleSwitch _tooltipWarnings = new() { OnContent = "Tooltip warnings shown", OffContent = "Tooltip warnings hidden" };
    readonly ToggleButton _sellingOn = new() { Content = "Selling" }, _buyingOn = new() { Content = "Buying" };
    readonly Slider _priceSlider;
    readonly DispatcherTimer _render;
    (MapleLite.Core.Imaging.Frame frame, Avalonia.Media.Imaging.Bitmap bitmap)? _card;
    bool _loading;   // filling controls from the session: their change handlers must not write back

    // Card Design controls that the monitor can change (IGN, channel, location).
    readonly TextBox _seller = new(), _channel = new(), _server = new(), _tagline = new(), _location = new(), _title = new(), _discord = new() { Watermark = "your Discord name" };
    // shown while the minimap hides the map's name, which leaves the location off the card
    readonly TextBlock _locationNote = Muted("Left off the card: your minimap is at its middle size, which hides the map's name. Show the name in the game to put it back.");

    public SaleCardPanel(SaleCardEngine engine, IPluginContext ctx)
    {
        _engine = engine;
        _ctx = ctx;
        _render = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _render!.Stop(); RenderCard(); });

        var copy = Button("Copy image", async () => await _engine.CopyCardImageAsync(), accent: true);
        var save = Button("Save PNG…", async () => await SaveAsync());
        _prices.IsCheckedChanged += (_, _) => { if (_loading) return; S.ShowPrices = _prices.IsChecked == true; S.HidePrices = !S.PricesShown; _engine.SettingsChanged(); };
        ToolTip.SetTip(_prices, "Show or leave out the prices on the card. Your prices stay in the list either way.");
        _tooltipWarnings.IsCheckedChanged += (_, _) => { if (_loading) return; S.HideMissingTooltipWarnings = _tooltipWarnings.IsChecked != true; _engine.SettingsChanged(); };
        ToolTip.SetTip(_tooltipWarnings, "Show missing-tooltip warnings on the card. Recorded details still appear when warnings are hidden.");
        Avalonia.Automation.AutomationProperties.SetName(_tooltipWarnings, "Show missing-tooltip warnings on the card");

        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Children = { _sellerLine, _gameLine } };
        // one click puts every selling item, or every item you're buying, on the card or takes them all off
        _sellingOn.IsCheckedChanged += (_, _) => { if (_loading) return; S.HideSelling = _sellingOn.IsChecked != true; _selectionHidden.IsVisible = S.HideSelling; _engine.SettingsChanged(); };
        _buyingOn.IsCheckedChanged += (_, _) => { if (_loading) return; S.HideBuying = _buyingOn.IsChecked != true; _buyHidden.IsVisible = S.HideBuying; _engine.SettingsChanged(); };
        ToolTip.SetTip(_sellingOn, "On: the items you're selling (the Selling tab) are on the card. Off: none of them are. Your ticks are kept either way.");
        ToolTip.SetTip(_buyingOn, "On: the items you're buying (the Buying tab) are on the card. Off: none of them are. Your ticks are kept either way.");
        var onCard = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "On the card:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }.Themed(TextBlock.ForegroundProperty, ThemeTokens.TextMuted), _sellingOn, _buyingOn } };
        var cardSwitches = Wrap(_prices, _tooltipWarnings);
        var actions = Wrap(onCard, cardSwitches, save, copy, Button("Export CSV…", async () => await ExportCsvAsync()), Button("Import CSV…", async () => await ImportCsvAsync()), Button("Clear card…", async () => await ClearCardAsync()));
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

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
        _sections.SizeChanged += (_, _) => _sections.Height = _sections.Bounds.Width < 860 ? 780 : BodyHeight;

        var maximize = Button("⛶  Enlarge", ToggleMaximized);
        _maximize = maximize;
        ToolTip.SetTip(maximize, "Shows the card across the whole page, as large as it fits. Press Reduce (or Esc) to bring the lists back.");
        // a bar above the card, so the button never covers part of it
        var previewBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var previewTitle = PaneTitle("Card preview");
        previewTitle.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(previewTitle, Dock.Left);
        previewBar.Children.Add(previewTitle);
        var popout = Button("Pop out ↗", OpenPopout);
        var editCard = new ToggleButton { Content = "Edit card" };
        editCard.IsCheckedChanged += (_, _) =>
        {
            _liveEditorScroll.IsVisible = editCard.IsChecked == true;
            LayoutCardEditor();
            _previewBox.Height = _liveEditorScroll.IsVisible ? 650 : _maximized ? 720 : 400;
            RefreshCardEditor();
        };
        var (priceBar, priceSlider) = CardPriceSlider(engine);
        _priceSlider = priceSlider;
        var kindPrices = new Button
        {
            Content = "Price adjustments…",
            Flyout = new Flyout
            {
                Placement = PlacementMode.Bottom,
                Content = new ScrollViewer { Content = _kindPrices, Width = 310, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            },
        };
        ToolTip.SetTip(kindPrices, "Adjust card prices by item type.");
        var previewTools = Wrap(priceBar, kindPrices, editCard, popout, maximize);
        previewTools.HorizontalAlignment = HorizontalAlignment.Right;
        previewTools.Margin = new Thickness(8, 0, 0, 0);
        previewBar.Children.Add(previewTools);
        var previewDock = new DockPanel();
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(actions, Dock.Bottom);
        previewDock.Children.Add(actions);
        DockPanel.SetDock(previewBar, Dock.Top);
        previewDock.Children.Add(previewBar);
        _previewContent.SizeChanged += (_, _) => LayoutCardEditor();
        _previewContent.Children.Add(_preview);
        Grid.SetColumn(_liveEditorScroll, 2); _previewContent.Children.Add(_liveEditorScroll);
        _liveEditorScroll.Content = _liveEditor;
        _liveEditor.LostFocus += (_, _) => Dispatcher.UIThread.Post(RefreshCardEditor, DispatcherPriority.Background);
        previewDock.Children.Add(_previewContent);
        var previewBox = Card(previewDock, 10);
        previewBox.Height = 400;
        _previewBox = previewBox;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape && _maximized) { ToggleMaximized(); e.Handled = true; } };
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,16,Auto") };
        Grid.SetRow(_sections, 0);
        Grid.SetRow(previewBox, 2);
        body.Children.Add(_sections);
        body.Children.Add(previewBox);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var foot = Stack(2, _status);
        foot.Margin = new Thickness(2, 8, 0, 0);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;

        void EngineChanged() => Dispatcher.UIThread.Post(() => { if (TopLevel.GetTopLevel(this) != null) OnEngineChanged(); });
        void CardChanged() => Dispatcher.UIThread.Post(QueueRender);
        AttachedToVisualTree += (_, _) => { _engine.Changed += EngineChanged; _engine.CardChanged += CardChanged; LoadFromSession(); OnEngineChanged(); QueueRender(); };
        DetachedFromVisualTree += (_, _) =>
        {
            _engine.Changed -= EngineChanged; _engine.CardChanged -= CardChanged; _render.Stop();
            _preview.Source = null; _card?.bitmap.Dispose(); _card = null;
        };
    }

    // ---------------------------------------------------------------- list

    Control BuildList()
    {
        _search.TextChanged += (_, _) => { if (_loading) return; S.Search = _search.Text ?? ""; RefreshList(); _engine.SettingsChanged(); };
        _source.SelectionChanged += (_, _) => { if (_loading) return; S.SellSource = _source.SelectedItem as string ?? "All sources"; RefreshList(); _engine.SettingsChanged(); };
        _type.SelectionChanged += (_, _) => { if (_loading) return; S.SellType = _type.SelectedItem as string ?? "All types"; RefreshList(); _engine.SettingsChanged(); };
        var sync = Button("Sync", () => { }, accent: true);
        sync.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            sync.IsEnabled = false;
            if (!await ConfirmWindow.AskAsync(owner, "Sync visible game items?",
                new[] { "Adds recognized visible Inventory, Storage and own-shop items to your card.",
                    "Updates scanned quantities. Items confirmed gone from visible slots may be removed.",
                    "Open each tab and scroll in game to read the rest. This does not wipe your card." },
                new[] { "Manually added items, unseen items and your Buying list",
                    "Recorded stats and custom prices on retained items. Details of removed scanned items may need to be read again." }, "Sync", "This updates")) { sync.IsEnabled = true; return; }
            sync.Content = "Syncing…";
            try { await _engine.RefreshNowAsync(); }
            catch (Exception ex) { _engine.Report("Sync failed: " + ex.Message); }
            finally { sync.IsEnabled = true; sync.Content = "Sync"; RefreshList(); }
        };
        ToolTip.SetTip(sync, "Refresh visible inventory, storage or your own shop. Open each tab in game to read it. Recognized visible items are added to your card. Manual and unseen items are kept.");
        var add = Button("+ Add item", async () => await AddItemAsync());
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { sync, add } };
        var help = Muted("Sync adds visible game items to your card. Hover equipment to read its stats.");
        var toolbar = new DockPanel { Margin = new Thickness(0, 4, 0, 12) };
        DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions); toolbar.Children.Add(help);
        help.VerticalAlignment = VerticalAlignment.Center;
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto,8,Auto"), Margin = new Thickness(0, 10, 0, 12) };
        filters.Children.Add(_search); Grid.SetColumn(_source, 2); filters.Children.Add(_source); Grid.SetColumn(_type, 4); filters.Children.Add(_type);
        _list.ItemsSource = _shown;
        _list.ItemTemplate = new FuncDataTemplate<SaleItem>((item, _) => AvailableRow(item!));
        _selectedList.ItemsSource = _selected;
        _selectedList.ItemTemplate = new FuncDataTemplate<SaleItem>((item, _) => Row());
        foreach (var review in new[] { _availablePrices, _selectedPrices })
            review.Click += (_, _) => { _reviewPrices = !_reviewPrices; RefreshList(force: true); };
        var available = ListPane(PaneTitle("Available items"), Stack(4, filters, _availablePrices, SelectedHeaders()), _list, _empty);
        _missingStats.Click += (_, _) => { _missingOnly = !_missingOnly; RefreshList(force: true); };
        ToolTip.SetTip(_missingStats, "Show selected equipment or scrolls whose stats are incomplete. Each row has a Read stats button.");
        var selected = ListPane(_selectedTitle, Stack(4, SelectedFilters(false), _selectionHidden, Wrap(_missingStats, _selectedPrices), SelectedHeaders()), _selectedList, _selectedEmpty,
            Muted("Removing an item here only removes it from the card."));
        var columns = SplitLists(available, selected);
        var dock = new DockPanel(); DockPanel.SetDock(toolbar, Dock.Top); dock.Children.Add(toolbar); dock.Children.Add(columns);
        _selectedList.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => { if (_listDirty && !_selectedList.IsKeyboardFocusWithin) RefreshList(); }, DispatcherPriority.Background);
        return dock;
    }

    static TextBlock PaneTitle(string label) => Text(label, size: 18, weight: FontWeight.SemiBold);

    readonly List<(Button button, string key, string label, bool buying)> _columnButtons = new();
    Control SelectedHeaders(bool buying = false)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,64,92,72"), Margin = new Thickness(0, 8, 8, 2) };
        foreach (var (key, label, col) in new[] { ("name", "Name", 0), ("qty", "Qty", 1), ("price", "Price", 2) })
        {
            var button = Button(label, () =>
            {
                string current = buying ? S.BuySort : S.ListSort;
                bool down = buying ? S.BuySortDown : S.ListSortDown;
                if (current != key) { current = key; down = false; }
                else if (!down) down = true;
                else { current = ""; down = false; }
                if (buying) { S.BuySort = current; S.BuySortDown = down; }
                else { S.ListSort = current; S.ListSortDown = down; }
                UpdateColumnHeaders(); RefreshList(); RefreshBuying(); _engine.SettingsChanged();
            });
            button.Padding = new Thickness(4, 2); button.FontSize = 12;
            ToolTip.SetTip(button, "Click for ascending, descending, then original order. Changes the list only.");
            Grid.SetColumn(button, col); row.Children.Add(button); _columnButtons.Add((button, key, label, buying));
        }
        UpdateColumnHeaders(); return row;
    }

    void UpdateColumnHeaders()
    {
        foreach (var (button, key, label, buying) in _columnButtons)
            button.Content = (buying ? S.BuySort : S.ListSort) == key ? label + ((buying ? S.BuySortDown : S.ListSortDown) ? " ▼" : " ▲") : label;
    }

    Control SelectedFilters(bool buying)
    {
        var search = new TextBox { Watermark = buying ? "Search buying card…" : "Search selling card…" };
        var type = new ComboBox { MinWidth = 120, ItemsSource = new[] { "All types" }.Concat(Categories.Order).ToArray() };
        var source = new ComboBox { MinWidth = 120, ItemsSource = ItemSources };
        void Load() { search.Text = buying ? S.BuyCardSearch : S.CardSearch; type.SelectedItem = buying ? S.BuyCardType : S.CardType; source.SelectedItem = S.CardSource; }
        Load(); _loaders.Add(Load);
        void Changed()
        {
            if (_loading) return;
            if (buying) { S.BuyCardSearch = search.Text ?? ""; S.BuyCardType = type.SelectedItem as string ?? "All types"; RefreshBuying(); }
            else { S.CardSearch = search.Text ?? ""; S.CardType = type.SelectedItem as string ?? "All types"; S.CardSource = source.SelectedItem as string ?? "All sources"; RefreshList(); }
            _engine.SettingsChanged();
        }
        search.TextChanged += (_, _) => Changed(); type.SelectionChanged += (_, _) => Changed(); source.SelectionChanged += (_, _) => Changed();
        var filters = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; if (!buying) filters.Children.Add(source); filters.Children.Add(type);
        return Stack(4, search, filters);
    }

    static Control ListPane(Control title, Control filters, Control list, Control empty, Control? footer = null)
    {
        var dock = new DockPanel();
        list.Margin = new Thickness(0, 0, 10, 0);
        var head = Stack(0, title, filters); DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
        if (footer != null) { footer.Margin = new Thickness(0, 10, 0, 0); DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer); }
        empty.Margin = new Thickness(16, 24); empty.HorizontalAlignment = HorizontalAlignment.Center;
        dock.Children.Add(new Panel { Children = { empty, new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } } });
        return Card(dock, 14);
    }

    static Grid SplitLists(Control available, Control selected)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*") };
        grid.Children.Add(available); Grid.SetColumn(selected, 2); grid.Children.Add(selected);
        bool? stacked = null;
        grid.SizeChanged += (_, _) =>
        {
            bool narrow = grid.Bounds.Width < 860;
            if (stacked == narrow) return;
            stacked = narrow;
            grid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,14,*");
            grid.RowDefinitions = new RowDefinitions(narrow ? "*,14,*" : "*");
            Grid.SetColumn(selected, narrow ? 0 : 2); Grid.SetRow(selected, narrow ? 2 : 0);
        };
        return grid;
    }

    Control AvailableRow(SaleItem item)
    {
        var add = Button("+ Add", () => { _engine.SetIncluded(new[] { item }, true); RefreshList(); }, accent: true);
        add.VerticalAlignment = VerticalAlignment.Center;
        var name = Text(item.Name, size: 14, weight: FontWeight.SemiBold); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        var words = Stack(3, name, Muted(CardSelection.SourceOf(item)), PriceReviewLabel(item)); words.VerticalAlignment = VerticalAlignment.Center;
        var remove = Button("×", () => { _engine.DismissAvailable(item); RefreshList(force: true); });
        ToolTip.SetTip(remove, "Remove this entry from Available items");
        var flag = PriceFlag(item);
        var price = new TextBox { Watermark = "Price", Width = 86, DataContext = item,
            [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Custom)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children = { price, flag, remove, add } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 10) };
        row.Children.Add(ItemIcon(item.ItemId)); Grid.SetColumn(words, 1); row.Children.Add(words); Grid.SetColumn(actions, 2); row.Children.Add(actions);
        return new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1) }.Themed(Border.BorderBrushProperty, ThemeTokens.Border);
    }

    static Control PriceReviewLabel(SaleItem? item = null)
    {
        var label = Muted("");
        label[!TextBlock.TextProperty] = new Binding(nameof(SaleItem.PriceReviewText));
        if (item != null) label.DataContext = item;
        return label;
    }

    static ToggleButton PriceFlag(SaleItem? item = null)
    {
        var flag = new ToggleButton { Content = "⚑", Padding = new Thickness(7, 4), VerticalAlignment = VerticalAlignment.Center,
            [!ToggleButton.IsCheckedProperty] = new Binding(nameof(SaleItem.PriceFlagged)) { Mode = BindingMode.TwoWay } };
        if (item != null) flag.DataContext = item;
        ToolTip.SetTip(flag, "Flag or unflag this item for price review");
        return flag;
    }

    /// <summary>A price-percent slider (1–200%) with its figure beside it, as a button that can put it back to 100%.</summary>
    static (StackPanel panel, Slider slider, Button label) PercentSlider(double width, string sliderTip, string labelTip)
    {
        var slider = new Slider { Minimum = 1, Maximum = 200, Value = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Width = width, VerticalAlignment = VerticalAlignment.Center };
        var label = new Button { Content = "100%", MinWidth = 50, Padding = new Thickness(4, 2), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(slider, sliderTip);
        ToolTip.SetTip(label, labelTip);
        return (new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { slider, label } }, slider, label);
    }

    internal static (StackPanel panel, Slider slider) CardPriceSlider(SaleCardEngine engine)
    {
        var (controls, slider, percent) = PercentSlider(150,
            "Adjusts every selling price and the total on the card as a percentage of the list prices.",
            "Reset the overall card price adjustment to 100%.");
        Avalonia.Automation.AutomationProperties.SetName(slider, "Overall card price adjustment");
        Avalonia.Automation.AutomationProperties.SetName(percent, "Reset overall card price adjustment");
        slider.ValueChanged += (_, _) =>
        {
            int value = (int)Math.Round(slider.Value);
            percent.Content = $"{value}%";
            if (value == Math.Clamp(engine.S.PricePercent, 1, 200)) return;
            engine.S.PricePercent = value;
            engine.SettingsChanged();
        };
        percent.Click += (_, _) => slider.Value = 100;
        slider.Value = Math.Clamp(engine.S.PricePercent, 1, 200);
        var label = Muted("Prices");
        label.VerticalAlignment = VerticalAlignment.Center;
        return (new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { label, controls } }, slider);
    }

    /// <summary>One item: tick, icon, name and going rate, price %, quantity, price, and a chevron for name, stats, slots and links.</summary>
    Control Row()
    {
        TextBox Box(string path, double width, string watermark, string tip)
        {
            var b = new TextBox { Width = width, Watermark = watermark, VerticalAlignment = VerticalAlignment.Center, [!TextBox.TextProperty] = new Binding(path) { Mode = BindingMode.TwoWay } };
            ToolTip.SetTip(b, tip);
            return b;
        }
        var include = new Button { Content = "−", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 4), Margin = new Thickness(6, 0, 0, 0) };
        ToolTip.SetTip(include, "Remove from card");
        include.Click += (_, _) => { if (include.DataContext is SaleItem it) { _engine.SetIncluded(new[] { it }, false); _selectedList.Focus(); RefreshList(force: true); } };
        var icon = new Image { Width = 30, Height = 30, [!Image.SourceProperty] = new Binding(nameof(SaleItem.Icon)) };
        RenderOptions.SetBitmapInterpolationMode(icon, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        var tile = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.Parse("#E1DFD5")), Margin = new Thickness(8, 0, 10, 0), Child = icon };
        var name = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, [!TextBlock.TextProperty] = new Binding(nameof(SaleItem.Name)) }
            .Themed(TextBlock.ForegroundProperty, ThemeTokens.Text);
        var rate = new RatePicker();   // the going rate, as buttons that put a figure in the price box
        var source = Muted("");
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3, Children = { name, source, PriceReviewLabel() } };
        rate.Margin = new Thickness(52, 2, 0, 0);
        var qty = Box(nameof(SaleItem.Qty), 56, "qty", "Quantity");
        var price = Box(nameof(SaleItem.Custom), 86, "price", "Your price. Leave it blank to use the going rate (or Offer when there is none), or click a figure under the name to put it here.");
        var more = new ToggleButton { Content = "▾", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(more, "Name, stats and slots");
        var adjust = PercentSlider(80,
            "Raises or lowers this item's price on the card (and in the total) by a percent, on top of its kind's and the card's slider. Your list keeps its price.",
            "This item's price on the card as a percent. Click to put it back to 100%.");
        adjust.panel.Margin = new Thickness(8, 0, 0, 0);
        bool settingPercent = false;
        adjust.slider.ValueChanged += (_, _) =>
        {
            int pct = (int)Math.Round(adjust.slider.Value);
            adjust.label.Content = pct + "%";
            if (!settingPercent && adjust.slider.DataContext is SaleItem it && it.PricePercent != pct) it.PricePercent = pct;
        };
        adjust.label.Click += (_, _) => adjust.slider.Value = 100;
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto,Auto") };
        foreach (var (c, i) in new Control[] { tile, words, qty, price, PriceFlag(), more, include }.Select((c, i) => (c, i))) { Grid.SetColumn(c, i); line.Children.Add(c); }
        price.Margin = new Thickness(6, 0, 0, 0); qty.Margin = new Thickness(8, 0, 0, 0);

        // details: name, stats, slots, read tooltip, look it up
        var details = new StackPanel { Spacing = 4, Margin = new Thickness(52, 8, 0, 4) };
        details.Children.Add(Muted("Price adjustment for this item"));
        details.Children.Add(adjust.panel);
        details.Children.Add(Muted("Name (edit if wrong)"));
        details.Children.Add(new TextBox { [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Name)) { Mode = BindingMode.TwoWay } });
        var equip = new StackPanel { Spacing = 4 };
        equip.Children.Add(Muted("Stats (include + / − and base rolls)"));
        equip.Children.Add(new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Stats)) { Mode = BindingMode.TwoWay } });
        var slotsLabel = Muted("Upgrade slots remaining");
        equip.Children.Add(slotsLabel);
        var slots = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left, [!TextBox.TextProperty] = new Binding(nameof(SaleItem.Slots)) { Mode = BindingMode.TwoWay } };
        ToolTip.SetTip(slots, "The number beside Remaining Enhancements. Blank means unknown. N/A means the tooltip has no upgrade slots.");
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
        var hideMissingTooltip = new CheckBox { Content = "Hide missing-tooltip message" };
        hideMissingTooltip[!ToggleButton.IsCheckedProperty] = new Binding(nameof(SaleItem.HideMissingTooltip)) { Mode = BindingMode.TwoWay };
        ToolTip.SetTip(hideMissingTooltip, "Hide the warning for this item. Recorded details still appear on the card.");
        equip.Children.Add(hideMissingTooltip);
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
        // when a scan named it wrongly: pick the real item; items added by hand can be taken off the list
        var change = new Button { Content = "Change item…", FontSize = 11, Margin = new Thickness(0, 0, 6, 4) };
        ToolTip.SetTip(change, "Wrong item? Search the item database for the right one. The fix sticks: later scans of this slot (or this icon) give the item you picked, and hovering it won't rename it.");
        change.Click += async (s, _) => { if ((s as Control)?.DataContext is SaleItem it) await ChangeItemAsync(it); };
        var remove = new Button { Content = "Remove from list", FontSize = 11, Margin = new Thickness(0, 0, 6, 4) };
        ToolTip.SetTip(remove, "Takes this item you added by hand off the list");
        remove.Click += (s, _) => { if ((s as Control)?.DataContext is SaleItem it) _engine.RemoveAdded(it); };
        details.Children.Add(new WrapPanel { Margin = new Thickness(0, 2, 0, 0), Children = { change, remove } });
        details[!IsVisibleProperty] = more[!ToggleButton.IsCheckedProperty];
        details.Children.Add(rate);
        var missingLabel = Muted("Stats missing — hover this item in-game with its tooltip visible.");
        var readMissing = new Button { Content = "Read stats (5s)", HorizontalAlignment = HorizontalAlignment.Left };
        readMissing.Click += async (_, _) =>
        {
            if (readMissing.DataContext is not SaleItem item) return;
            if (item.IsAdded) { more.IsChecked = true; return; }
            readMissing.IsEnabled = false;
            try { await _engine.ReadEquipmentAsync(item); }
            catch (Exception ex) { _engine.Report("Couldn't read stats: " + ex.Message); }
            finally { readMissing.IsEnabled = true; RefreshList(); }
        };
        var hideWarning = new Button { Content = "Hide warning", HorizontalAlignment = HorizontalAlignment.Left };
        hideWarning.Click += (_, _) =>
        {
            if (hideWarning.DataContext is SaleItem item) item.HideMissingTooltip = true;
        };
        var missingNotice = Stack(4, missingLabel, Wrap(readMissing, hideWarning));
        missingNotice.Margin = new Thickness(52, 6, 0, 4);
        missingNotice[!IsVisibleProperty] = new Binding(nameof(SaleItem.MissingStats));
        var border = new Border { Child = Stack(3, line, missingNotice, details), Padding = new Thickness(0, 12), BorderThickness = new Thickness(0, 0, 0, 1) }.Themed(Border.BorderBrushProperty, ThemeTokens.Border);
        border.Margin = new Thickness(0, 0, 0, 4);

        border.DataContextChanged += (_, _) =>
        {
            var it = border.DataContext as SaleItem;
            readMissing.Content = it?.IsAdded == true ? "Enter stats" : "Read stats (5s)";
            missingLabel.Text = it?.IsAdded == true ? "Stats missing — enter the stats for this manually added item." : "Stats missing — hover this item in-game with its tooltip visible.";
            source.Text = it == null ? "" : CardSelection.SourceOf(it) + (it.IsEquipment && it.SlotRow >= 0 && it.SlotCol >= 0 ? $" · row {it.SlotRow + 1}, slot {it.SlotCol + 1}" : "");
            settingPercent = true;
            adjust.slider.Value = it?.PricePercent ?? 100;
            settingPercent = false;
            equip.IsVisible = it is { IsEquipment: true } or { IsScroll: true };
            // scrolls have a success rate and an effect, not upgrade slots
            slotsLabel.IsVisible = slots.IsVisible = it is { IsEquipment: true };
            remove.IsVisible = it is { IsAdded: true };
            readTip.IsVisible = it is not { IsAdded: true };   // nothing in the game to hover for it
        };
        return border;
    }


    bool _listDirty;

    // ---------------------------------------------------------------- buying: the whole item database

    const int BuyResultsMax = 120;
    readonly TextBox _buySearch = new() { Watermark = "Search the item database…", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly StackPanel _buyRows = new() { Spacing = 4 };
    readonly StackPanel _buySelectedRows = new() { Spacing = 4 };
    readonly TextBlock _buySelectedTitle = PaneTitle("On your card · 0");
    readonly TextBlock _buySelectedEmpty = Muted("Search for an item, then press + Add to start your Buying list.");
    readonly TextBlock _buyHidden = Muted("Buying is hidden from the exported card. Turn on Buying in the preview to show these items.");
    readonly ComboBox _buyType = new() { MinWidth = 145, ItemsSource = new[] { "All types" }.Concat(Categories.Order).ToArray(), SelectedIndex = 0 };
    string _buySelectedShown = "\0";
    readonly TextBlock _buyNote = Muted("");
    readonly Button _buyPrices = new();
    bool _reviewBuyingPrices;
    string _buyShown = "";   // what the rows show now, to rebuild only when it changes

    /// <summary>
    /// The Buying tab: search every item in the game's catalog and tick the ones you want. Each ticked item is a card
    /// tagged BUYING, with its own quantity and price (the going rate shows until you type one). What you're buying is
    /// listed first; the matches for the search follow.
    /// </summary>
    Control BuildBuying()
    {
        _buySearch.TextChanged += (_, _) => { if (_loading) return; S.BuySearch = _buySearch.Text ?? ""; RefreshBuying(); _engine.SettingsChanged(); };
        _buyType.SelectionChanged += (_, _) => { if (_loading) return; S.BuyType = _buyType.SelectedItem as string ?? "All types"; RefreshBuying(); _engine.SettingsChanged(); };
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto"), Margin = new Thickness(0, 10, 0, 10) };
        filters.Children.Add(_buySearch); Grid.SetColumn(_buyType, 2); filters.Children.Add(_buyType);
        var available = ListPane(PaneTitle("Item database"), Stack(0, filters, _buyNote), _buyRows, new TextBlock());
        _buyPrices.Click += (_, _) => { _reviewBuyingPrices = !_reviewBuyingPrices; RefreshBuying(force: true); };
        var selected = ListPane(_buySelectedTitle, Stack(4, SelectedFilters(true), _buyHidden, _buyPrices, SelectedHeaders(true)), _buySelectedRows, _buySelectedEmpty,
            Muted("Set how many you want and your offer. Expand an item to add stat requirements."));
        var dock = new DockPanel();
        var help = Muted("Find what you want to buy, then add it to your card. Your inventory is not changed.");
        help.Margin = new Thickness(0, 8, 0, 16); DockPanel.SetDock(help, Dock.Top); dock.Children.Add(help);
        dock.Children.Add(SplitLists(available, selected));
        _buySelectedRows.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => RefreshBuying(), DispatcherPriority.Background);
        return dock;
    }

    void RefreshBuying(bool force = false)
    {
        if (_engine.Vision is not { } vision) return;
        var q = (S.BuySearch ?? "").Trim();
        var buying = _engine.Items.Where(i => i.IsWanted).ToList();
        var have = buying.Where(i => i.Include).Select(i => i.ItemId).ToHashSet();
        var matches = CardSelection.CatalogMatches(vision.Catalog.Values, q, S.BuyType, have).ToList();
        var signature = string.Join(",", have) + "|" + q + "|" + S.BuyType;
        if (force || signature != _buyShown)
        {
            _buyShown = signature;
            _buyRows.Children.Clear();
            foreach (var cat in matches.Take(BuyResultsMax)) _buyRows.Children.Add(CatalogRow(cat));
            if (matches.Count == 0) _buyRows.Children.Add(Muted("No available items match. Try another name or item type."));
        }
        _buyPrices.Content = _reviewBuyingPrices ? "Show all prices" : $"Review prices · {buying.Count(i => i.Include && (i.MissingPrice || i.PriceFlagged))}";
        var filtered = Sorted(buying.Where(i => CardSelection.Selected(i, true, S.BuyCardSearch, null, S.BuyCardType)
            && (!_reviewBuyingPrices || i.MissingPrice || i.PriceFlagged)), true).ToList();
        var selectedSignature = string.Join(",", filtered.Select(i => i.Key));
        if ((force || selectedSignature != _buySelectedShown) && (force || !_buySelectedRows.IsKeyboardFocusWithin))
        {
            _buySelectedShown = selectedSignature;
            _buySelectedRows.Children.Clear();
            foreach (var card in filtered) _buySelectedRows.Children.Add(BuyRow(card));
        }
        _buySelectedTitle.Text = $"On your card · {have.Count}";
        _buySelectedEmpty.IsVisible = filtered.Count == 0;
        _buySelectedEmpty.Text = have.Count > 0 ? "No selected items match these filters." : "Search for an item, then press + Add to start your Buying list.";
        _buyHidden.IsVisible = S.HideBuying;
        _buyNote.Text = matches.Count > BuyResultsMax ? $"Showing {BuyResultsMax} of {matches.Count:N0} items. Search to narrow the list." : $"{matches.Count:N0} available items";
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
        var tick = Button("−", () =>
        {
            if (_engine.Vision?.Catalog.TryGetValue(card.ItemId, out var cat) == true) _engine.SetBuying(cat, false);
            RefreshBuying(force: true);
        });
        tick.VerticalAlignment = VerticalAlignment.Center;
        tick.Padding = new Thickness(8, 4); tick.Margin = new Thickness(6, 0, 0, 0);
        ToolTip.SetTip(tick, "Remove from Buying card");
        var name = new TextBlock { DataContext = card, [!TextBlock.TextProperty] = new Binding(nameof(SaleItem.Name)), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }.Themed(TextBlock.ForegroundProperty, ThemeTokens.Text);
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
        foreach (var c in new Control[] { icon }) { DockPanel.SetDock(c, Dock.Left); line.Children.Add(c); }
        foreach (var c in new Control[] { tick, more, PriceFlag(), price, qty }) { DockPanel.SetDock(c, Dock.Right); line.Children.Add(c); }
        line.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name } });
        var details = WantedEditor(card);
        details[!IsVisibleProperty] = more[!ToggleButton.IsCheckedProperty];
        var expanded = Stack(4, rate, details);
        expanded[!IsVisibleProperty] = more[!ToggleButton.IsCheckedProperty];
        return new FitWidth { Child = new Border { Child = Stack(3, line, PriceReviewLabel(card), wants, expanded), Padding = new Thickness(0, 12), BorderThickness = new Thickness(0, 0, 0, 1) }.Themed(Border.BorderBrushProperty, ThemeTokens.Border) };
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
        var add = Button("+ Add", () => { _engine.SetBuying(cat, true); RefreshBuying(force: true); }, accent: true);
        add.VerticalAlignment = VerticalAlignment.Center;
        var name = Text(cat.name, size: 14, weight: FontWeight.SemiBold); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        var words = Stack(3, name, Muted(Categories.Of(cat.id))); words.VerticalAlignment = VerticalAlignment.Center;
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 10) };
        line.Children.Add(ItemIcon(cat.id)); Grid.SetColumn(words, 1); line.Children.Add(words); Grid.SetColumn(add, 2); line.Children.Add(add);
        return new Border { Child = line, BorderThickness = new Thickness(0, 0, 0, 1) }.Themed(Border.BorderBrushProperty, ThemeTokens.Border);
    }

    // ---------------------------------------------------------------- sorting the list

    /// <summary>The list in the chosen column order; ties (and no sort) keep the order the items were found in.</summary>
    IEnumerable<SaleItem> Sorted(IEnumerable<SaleItem> items, bool buying = false)
    {
        var list = items.ToList();
        string sort = buying ? S.BuySort : S.ListSort;
        bool down = buying ? S.BuySortDown : S.ListSortDown;
        static long Price(SaleItem i) => (!string.IsNullOrWhiteSpace(i.Custom) ? Mesos.Parse(i.Custom) : i.RateValue) ?? -1;
        IOrderedEnumerable<SaleItem>? o = sort switch
        {
            "on" => down ? list.OrderBy(i => i.Include) : list.OrderByDescending(i => i.Include),
            "name" => down ? list.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase) : list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "qty" => down ? list.OrderByDescending(i => i.Pieces ?? 0) : list.OrderBy(i => i.Pieces ?? 0),
            // Offer (no price) always last, both ways
            "price" => down ? list.OrderBy(i => Price(i) < 0).ThenByDescending(Price) : list.OrderBy(i => Price(i) < 0).ThenBy(Price),
            _ => null,
        };
        return o ?? (IEnumerable<SaleItem>)list;
    }

    void RefreshList(bool force = false)
    {
        static bool NeedsReview(SaleItem item) => item.MissingPrice || item.PriceFlagged;
        var available = Sorted(_engine.Items.Where(i => CardSelection.Available(i, S.Search, S.SellSource, S.SellType))).ToList();
        var allSelected = Sorted(_engine.Items.Where(i => !i.IsWanted && i.Include)).ToList();
        _availablePrices.Content = _reviewPrices ? "Show all prices" : $"Review prices · {available.Count(NeedsReview)}";
        _selectedPrices.Content = _reviewPrices ? "Show all prices" : $"Review prices · {allSelected.Count(NeedsReview)}";
        if (_reviewPrices) available = available.Where(NeedsReview).ToList();
        int missing = allSelected.Count(i => i.MissingStats);
        _missingStats.IsVisible = missing > 0 || _missingOnly;
        _missingStats.Content = _missingOnly ? $"Missing stats · {missing} — Show all" : $"Missing stats · {missing} — Review items";
        var selected = allSelected.Where(i => CardSelection.Selected(i, false, S.CardSearch, S.CardSource, S.CardType)
            && (!_missingOnly || i.MissingStats) && (!_reviewPrices || NeedsReview(i))).ToList();
        Replace(_shown, available);
        if (force || !_selectedList.IsKeyboardFocusWithin || selected.SequenceEqual(_selected))
        { Replace(_selected, selected); _listDirty = false; }
        else _listDirty = true;
        _selectedTitle.Text = $"On your card · {allSelected.Count}";
        _selectedEmpty.Text = _missingOnly && missing == 0 ? "No missing stats to review." : allSelected.Count > 0 && selected.Count == 0 ? "No selected items match these filters." : "Sync or use + Add to put items on your card.";
        _selectionHidden.IsVisible = S.HideSelling;
        _loading = true; _sellingOn.IsChecked = !S.HideSelling; _loading = false;
        _selectedEmpty.IsVisible = selected.Count == 0;
        _empty.IsVisible = available.Count == 0;
        _empty.Text = S.SellSource == "Equipped" ? "Record worn gear in DPS Simulator using the Equipment window tooltips."
            : _reviewPrices ? "No available items match price review and the current filters."
            : _engine.Items.Any(i => !i.IsWanted)
            ? "No available items match these filters. Items already on your card are shown on the right."
            : "Open your inventory, storage or shop in game, then Sync. Or use + Add item.";
    }

    static void Replace(ObservableCollection<SaleItem> target, List<SaleItem> items)
    {
        if (items.SequenceEqual(target)) return;
        target.Clear(); foreach (var item in items) target.Add(item);
    }

    /// <summary>
    /// One price-percent slider per kind of item present. Rebuilt only when the kinds change, so the list refreshing
    /// (as it does while a slider moves) never replaces the slider being dragged.
    /// </summary>
    void RebuildKindPrices(List<string> kinds)
    {
        string key = string.Join("|", kinds);
        if (key == _kindPricesFor) return;
        _kindPricesFor = key;
        _kindPrices.Children.Clear();
        if (kinds.Count == 0) _kindPrices.Children.Add(Muted("Add selling items to adjust their prices by type."));
        foreach (var kind in kinds)
        {
            var (panel, slider, label) = PercentSlider(110,
                $"Raises or lowers the card price of every {kind} item by a percent, on top of the card's slider. Your list keeps its prices.",
                $"{kind} prices on the card as a percent. Click to put it back to 100%.");
            slider.Value = _engine.KindPercent(kind);
            label.Content = _engine.KindPercent(kind) + "%";
            slider.ValueChanged += (_, _) =>
            {
                int pct = (int)Math.Round(slider.Value);
                label.Content = pct + "%";
                if (!_loading) _engine.SetKindPercent(kind, pct);
            };
            label.Click += (_, _) => slider.Value = 100;
            var name = new TextBlock { Text = kind, Width = 118, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                .Themed(TextBlock.ForegroundProperty, ThemeTokens.Text);
            _kindPrices.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { name, panel } });
        }
    }

    void RebuildFilters()
    {
        var groups = Categories.Order.Where(c => _engine.Items.Any(i => !i.IsWanted && i.Category == c)).ToList();
        RebuildKindPrices(groups);
        var options = new[] { "All types" }.Concat(Categories.Order).ToList();
        _loading = true;
        try
        {
            if (!_type.Items.Cast<string>().SequenceEqual(options)) _type.ItemsSource = options;
            _type.SelectedItem = options.Contains(S.SellType) ? S.SellType : "All types";
            _source.SelectedItem = _source.Items.Cast<string>().Contains(S.SellSource) ? S.SellSource : "All sources";
        }
        finally { _loading = false; }
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

        var tagColours = Stack(4,
            Muted("Tag colours"),
            ColourRow("SALE", () => S.SaleTagColour, v => S.SaleTagColour = v),
            ColourRow("BUYING", () => S.BuyTagColour, v => S.BuyTagColour = v),
            ColourRow("IN SHOP", () => S.ShopTagColour, v => S.ShopTagColour = v),
            ColourRow("Discount", () => S.DiscountTagColour, v => S.DiscountTagColour = v));
        tagColours.Margin = new Thickness(25, 2, 0, 6);
        var hideOffer = Check("Hide \"Offer\"", () => S.HideOffer, v => S.HideOffer = v, "Items with no price show nothing instead of Offer, while the other prices still show.");
        var show = Stack(0,
            Check("Character picture", () => !S.NoAvatar, v => S.NoAvatar = !v, "Shows your character picture. Take character portrait lets you draw one by hand."),
            Check("Minimap", () => !S.NoMap, v => S.NoMap = !v, "The minimap picture, with the dot for where you stand. Read while the minimap is open at full or middle size."),
            Check("Total", () => !S.NoTotal, v => S.NoTotal = !v, "The total for everything on the card (price each × quantity). Items priced Offer are counted separately."),
            Check("Total per kind", () => !S.NoKindTotals, v => S.NoKindTotals = !v,
                "Splits the total by kind of item (Armor 200k, Weapons 50k, Gems & ores 45k), with the overall total under it. Shows when the card has more than one kind."),
            Check("Group items by kind", () => !S.NoGroups, v => S.NoGroups = !v,
                "Puts weapons, armor, gems & ores and so on in groups of their own, each under a thin divider in the card's accent colour with the kind's name."),
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
            Check("Discount tag", () => !S.NoDiscountTags, v => S.NoDiscountTags = !v,
                "Puts the discount (\"-20%\") on each priced item you sell while the price slider is below 100%. Hidden at 100% and above."),
            tagColours,
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
            Muted("Location"), Field(_location, v => S.Location = v), _locationNote,
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
        var openChecks = Button("Open hover checks (Developer)", () => OpenFolder(_engine.DiagnosticsFolder));
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
            Head("List"), Wrap(refresh, clear, ShopBack(), Button("Restore dismissed items", _engine.RestoreDismissed)),
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
        UpdateColumnHeaders();
        _kindPricesFor = "\0";   // settings (re)loaded: the kind sliders are built again with their saved values
        _loading = true;
        try
        {
            _search.Text = S.Search;
            _prices.IsChecked = S.PricesShown;
            _tooltipWarnings.IsChecked = !S.HideMissingTooltipWarnings;
            _priceSlider.Value = Math.Clamp(S.PricePercent, 1, 200);
            _seller.Text = S.Seller; _channel.Text = S.Channel; _server.Text = S.Server; _tagline.Text = S.Tagline ?? ""; _discord.Text = S.Discord ?? "";
            _location.Text = S.Location; _title.Text = S.Title;
            foreach (var (box, get) in _checks) box.IsChecked = get();
            foreach (var load in _loaders) load();

            _buySearch.Text = S.BuySearch ?? "";
            _buyType.SelectedItem = S.BuyType;
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
            _locationNote.IsVisible = S.MapNameHidden;
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
        RebuildFilters();
        RefreshList();
        RefreshBuying();
    }

    // ---------------------------------------------------------------- card

    readonly Grid _previewContent = new() { ColumnDefinitions = new ColumnDefinitions("*,0,0") };
    readonly StackPanel _liveEditor = new() { Spacing = 8, Focusable = true };
    readonly ScrollViewer _liveEditorScroll = new() { IsVisible = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    List<SaleItem> _editing = new();

    void LayoutCardEditor()
    {
        bool narrow = _liveEditorScroll.IsVisible && _previewContent.Bounds.Width < 720;
        string columns = !_liveEditorScroll.IsVisible || narrow ? "*,0,0" : "3*,16,2*";
        string rows = narrow ? "230,12,*" : "*";
        if (_previewContent.ColumnDefinitions.ToString() != columns) _previewContent.ColumnDefinitions = new ColumnDefinitions(columns);
        if (_previewContent.RowDefinitions.ToString() != rows) _previewContent.RowDefinitions = new RowDefinitions(rows);
        Grid.SetColumn(_liveEditorScroll, narrow ? 0 : 2); Grid.SetRow(_liveEditorScroll, narrow ? 2 : 0);
    }

    void RefreshCardEditor()
    {
        if (!_liveEditorScroll.IsVisible || (_liveEditor.IsKeyboardFocusWithin && !_liveEditor.IsFocused)) return;
        var items = _engine.EditableCardItems();
        if (items.SequenceEqual(_editing) && _liveEditor.Children.Count > 0) return;
        _editing = items;
        _liveEditor.Children.Clear();
        _liveEditor.Children.Add(Muted("Edits save to the same items as your lists. Arrows select Manual order: individual entries, no grouping or column packing. Selling stays before Buying."));
        if (items.Count == 0) _liveEditor.Children.Add(Muted("No visible items on the card. Add items or enable Selling / Buying."));
        foreach (var item in items)
        {
            Control Field(string property, string label)
            {
                var box = new TextBox { Watermark = label, [!TextBox.TextProperty] = new Binding(property) { Mode = BindingMode.TwoWay } };
                return Stack(2, Muted(label), box);
            }
            void Move(int direction) { _engine.MoveCardItem(item, direction); LoadFromSession(); _liveEditor.Focus(); RefreshCardEditor(); }
            var up = Button("↑", () => Move(-1)); var down = Button("↓", () => Move(1));
            int index = items.IndexOf(item);
            up.IsEnabled = index > 0 && items[index - 1].IsWanted == item.IsWanted;
            down.IsEnabled = index + 1 < items.Count && items[index + 1].IsWanted == item.IsWanted;
            ToolTip.SetTip(up, "Move this item earlier on the card"); ToolTip.SetTip(down, "Move this item later on the card");
            var remove = Button("Remove", () =>
            {
                _engine.RemoveCardTile(item);
                _liveEditor.Focus(); RefreshCardEditor();
            });
            var fields = Stack(5, Muted(item.IsWanted ? "Buying" : "Selling"), Field(nameof(SaleItem.Name), "Item name"),
                Field(nameof(SaleItem.Qty), "Quantity"), Field(nameof(SaleItem.Custom), "Base price (blank = going rate)"),
                Field(nameof(SaleItem.Stats), item.IsWanted ? "Wanted stats" : "Stats"), Field(nameof(SaleItem.Slots), "Slots"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { up, down, remove } });
            fields.DataContext = item;
            _liveEditor.Children.Add(Card(fields, 10));
        }
    }

    void QueueRender()
    {
        _render.Stop();
        if (TopLevel.GetTopLevel(this) == null) return;
        _priceSlider.Value = Math.Clamp(S.PricePercent, 1, 200);
        RefreshList(); RefreshBuying(); RefreshCardEditor(); _render.Start();
    }

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
        Grid.SetRow(_previewBox, _maximized ? 0 : 2);
        Grid.SetRowSpan(_previewBox, _maximized ? 3 : 1);
        _previewBox.Height = _maximized ? 720 : _liveEditorScroll.IsVisible ? 650 : 400;
        _preview.StretchDirection = _maximized ? StretchDirection.Both : StretchDirection.DownOnly;
        _maximize.Content = _maximized ? "⤡  Reduce" : "⛶  Enlarge";
        ToolTip.SetTip(_maximize, _maximized ? "Back to the lists above the card (Esc)" : "Shows the card across the whole page, as large as it fits. Press Reduce (or Esc) to bring the lists back.");
        if (_maximized) { Focusable = true; Focus(); }
    }

    void RenderCard()
    {
        try
        {
            var (items, options) = _engine.Card();
            var previous = _card;
            _card = CardRenderer.Render(items, options);
            _preview.Source = _card.Value.bitmap;
            previous?.bitmap.Dispose();
            _cropper?.Refresh();   // the map may have refreshed, been trimmed or cropped
        }
        catch (Exception e) { _ctx.Log("card render failed: " + e); _status.Text = "Couldn't draw the card: " + e.Message; }
    }

    void OpenPopout()
    {
        if (TopLevel.GetTopLevel(this) is Window owner) _engine.OpenPopout(owner);
    }

    async Task ClearCardAsync()
    {
        if (TopLevel.GetTopLevel(this) is Window owner && _engine.Items.Any(i => i.Include)
            && await ConfirmWindow.AskAsync(owner, "Clear the card?",
                ["Takes all Selling and Buying items off the card."],
                ["Available items, saved prices and item details."], "Clear card"))
        {
            _engine.ClearCard();
            RefreshList(force: true);
            RefreshBuying(force: true);
        }
    }

    async Task ExportCsvAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export card item list (before card-wide discounts)", SuggestedFileName = $"MapleLite-card {DateTime.Now:yyyy-MM-dd HH-ss}.csv", DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try { await File.WriteAllTextAsync(path, CardCsv.Export(_engine.Items), new System.Text.UTF8Encoding(true)); _ctx.Notify("Exported card item list."); }
        catch (Exception e) { _ctx.Notify("CSV export failed: " + e.Message); }
    }

    async Task ImportCsvAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Merge CSV items into this card", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            if (new FileInfo(path).Length > 5_000_000) throw new FormatException("CSV exceeds 5 MB.");
            var csv = await File.ReadAllTextAsync(path);
            int count = _engine.ImportCsv(csv);
            _ctx.Notify($"Imported {count} items. Existing scanned items were kept.");
        }
        catch (Exception e) { _ctx.Notify("CSV import failed: " + e.Message); }
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

    async Task AddItemAsync()
    {
        if (_engine.Vision is not { } vision || TopLevel.GetTopLevel(this) is not Window owner) return;
        var pick = await ItemPickerWindow.ShowAsync(owner, "Add an item", "Search the item database. The item goes on your Selling list under Added by hand, where you set its quantity and price.",
            vision.Catalog.Values, _engine.IconOf, allowCustom: true, start: S.Search ?? "");
        if (pick == null) return;
        _engine.AddByHand(pick.Item, pick.Name);
        RefreshList(force: true); _engine.SettingsChanged();
    }

    async Task ChangeItemAsync(SaleItem item)
    {
        if (_engine.Vision is not { } vision || TopLevel.GetTopLevel(this) is not Window owner) return;
        var pick = await ItemPickerWindow.ShowAsync(owner, "Change item", $"Pick what \"{item.Name}\" really is. Its quantity, price and stats stay.",
            vision.Catalog.Values, _engine.IconOf, allowCustom: false);
        if (pick?.Item is { } cat) { _engine.ChangeItem(item, cat); _selected.Clear(); RefreshList(force: true); }
    }

    async Task<bool> Confirm(string question, string[] clears, string[] keeps, string go)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await ConfirmWindow.AskAsync(owner, question, clears, keeps, go);
    }
}

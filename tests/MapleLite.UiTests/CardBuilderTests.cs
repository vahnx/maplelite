using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MapleLite.Core.Game;
using MapleLite.Core.Capture;
using MapleLite.Core.Imaging;
using MapleLite.Host.Themes;
using MapleLite.Plugins.SaleCard;
using MapleLite.Sdk;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(MapleLite.UiTests.TestApp))]
namespace MapleLite.UiTests;

public class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public override void Initialize()
    {
        Environment.SetEnvironmentVariable("MAPLELITE_DATA", Path.Combine(Path.GetTempPath(), "MapleLite-ui-tests", Guid.NewGuid().ToString("N")));
        Styles.Add(new FluentTheme());
        new ThemeService().Apply("midnight");
    }
}

public class CardBuilderTests
{
    [AvaloniaFact]
    public void Card_exports_preserve_preview_colours_in_every_theme()
    {
        for (int theme = 0; theme < CardRenderer.Themes.Length; theme++)
        {
            var (frame, bitmap) = CardRenderer.Render(
                [new SaleItem { Name = "Colour test", Custom = "12345" }],
                new CardOptions { Theme = theme, Seller = "Seller", ShowTime = false });
            using (bitmap)
            {
                // Avalonia's own PNG writer is independent of our raw-pixel export path.
                using var preview = new MemoryStream();
                bitmap.Save(preview);
                var expected = FrameFiles.DecodePng(preview.ToArray());
                Assert.Equal(expected.Px, frame.Px);
                foreach (bool opaque in new[] { false, true }) // clipboard and Save PNG
                {
                    var exported = FrameFiles.DecodePng(FrameFiles.EncodePng(frame, opaque));
                    Assert.Equal(expected.Px, exported.Px);
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task Card_layout_picker_updates_the_shared_image_and_restores_the_selection()
    {
        Frame? copied = null;
        var storage = new Storage(Path.Combine(Path.GetTempPath(), "MapleLite-ui-tests", Guid.NewGuid().ToString("N")));
        var (window, panel, engine) = Open(storage, frame => { copied = frame; return Task.FromResult(true); });
        ComboBox LayoutPicker(SaleCardPanel page)
        {
            page.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
            Pump();
            return Visible<ComboBox>(page).Single(box => AutomationProperties.GetName(box) == "Card layout");
        }
        try
        {
            var picker = LayoutPicker(panel);
            Assert.Equal("Classic", picker.SelectedItem);
            picker.SelectedItem = "Showcase";
            engine.S.NoTime = true;
            var (items, options) = engine.Card();
            Assert.Equal(CardLayout.Showcase, options.Layout);

            var (expected, bitmap) = CardRenderer.Render(items, options);
            using (bitmap)
            {
                Capture(window, "Card-Design-Showcase");
                var preview = Visible<Image>(panel).Select(image => image.Source)
                    .OfType<Avalonia.Media.Imaging.RenderTargetBitmap>().Single();
                Assert.Equal(bitmap.PixelSize, preview.PixelSize);
                engine.OpenPopout(window);
                Pump();
                var popout = Assert.IsType<CardPopout>(Assert.Single(window.OwnedWindows));
                var popoutImage = Assert.Single(Visible<Image>(popout));
                Assert.Equal(bitmap.Size, popoutImage.Source!.Size);
                await engine.CopyCardImageAsync();
                Assert.Equal(expected.Px, Assert.IsType<Frame>(copied).Px);
            }
            engine.SaveSession();
        }
        finally { window.Close(); }

        var (reopened, page, restored) = Open(storage);
        try
        {
            Assert.Equal("Showcase", LayoutPicker(page).SelectedItem);
            Assert.Equal(CardLayout.Showcase, restored.Card().options.Layout);
        }
        finally { reopened.Close(); }
    }

    [AvaloniaFact]
    public void Styled_cards_keep_every_tile_and_its_price_inside_the_export()
    {
        var (window, _, engine) = Open();
        try
        {
            var items = engine.Items.ToArray();
            var equipment = items.First(i => i.IsEquipment);
            equipment.Stats = "Weapon attack +63\nAccuracy +4";
            equipment.Slots = "7";
            items[^1].Tab = SaleCardEngine.BuyTab;
            items[^1].Buying = true;
            items[^1].Custom = ""; // Offer can be hidden independently of the other tiles' prices.
            var map = new int[160 * 105];
            for (int y = 0; y < 105; y++)
                Array.Fill(map, y >= 87 || y is 35 or 65 ? Pixel.Rgb(125, 95, 64) : Pixel.Rgb(44, 68, 103), y * 160, 160);
            string[] dot = ["..##..", ".#++#.", "#+..+#", "#+..+#", ".#++#.", "..##.."];
            for (int y = 0; y < dot.Length; y++)
                for (int x = 0; x < dot[y].Length; x++)
                    if (dot[y][x] != '.')
                        map[(54 + y) * 160 + 103 + x] = dot[y][x] == '#' ? Pixel.Rgb(255, 238, 34) : Pixel.Rgb(255, 221, 68);
            Assert.NotNull(MapReader.PlayerDot(map, 160, 105));
            const string markerColour = "#00F1B9";
            int markerPixel = Pixel.Rgb(0, 241, 185);
            var avatar = new int[AvatarReader.W * AvatarReader.H];
            for (int y = 24; y < 68; y++)
                Array.Fill(avatar, y < 45 ? Pixel.Rgb(219, 162, 107) : Pixel.Rgb(73, 135, 124), y * AvatarReader.W + 23, 22);
            var examples = new (CardLayout Layout, string Theme)[]
            {
                (CardLayout.Showcase, "Twilight"), (CardLayout.MarketBoard, "Light"),
                (CardLayout.Ledger, "Maple"), (CardLayout.Stage, "Midnight"),
            };
            var sizes = new HashSet<PixelSize>();
            foreach (var (layout, theme) in examples)
            {
                var options = new CardOptions
                {
                    Layout = layout, Theme = Array.FindIndex(CardRenderer.Themes, t => t.Name == theme),
                    Seller = "CuteLilSin", Server = "Windia", Tagline = "Weekend restock", Channel = "14", Location = "Free Market 19",
                    Map = (map, 160, 105), SellerMarker = true, MarkerColours = new(Ring: markerColour, Tag: markerColour), Avatar = avatar,
                    HideOffer = true, ShowTime = false,
                };
                foreach (var mode in new (bool Pack, bool Groups, bool Compact)[] { (false, false, true), (true, true, true), (false, true, false) })
                {
                    var positions = new List<CardRenderer.TileLayout>();
                    var (frame, bitmap) = CardRenderer.Render(items,
                        options with { Pack = mode.Pack, Groups = mode.Groups, Compact = mode.Compact }, positions);
                    using (bitmap)
                    {
                        Assert.Contains(markerPixel, frame.Px);
                        Assert.Equal(items.Length, positions.Count);
                        foreach (var position in positions)
                        {
                            AssertInside(position.Tile, new Rect(0, 0, frame.W, frame.H));
                            if (position.Item.FinalPrice == "Offer") Assert.Null(position.Price);
                            else AssertInside(Assert.IsType<Rect>(position.Price), position.Tile);
                            Assert.DoesNotContain(positions, other => other != position &&
                                other.Tile.Intersect(position.Tile) is { Width: > 0.1, Height: > 0.1 });
                        }
                        if (!mode.Groups)
                        {
                            Assert.True(sizes.Add(bitmap.PixelSize));
                            var folder = Environment.GetEnvironmentVariable("MAPLELITE_UI_SCREENSHOTS");
                            if (!string.IsNullOrEmpty(folder))
                            {
                                Directory.CreateDirectory(folder);
                                bitmap.Save(Path.Combine(folder, $"{layout}.png"));
                            }
                        }
                    }
                }
            }
        }
        finally { window.Close(); }

        static void AssertInside(Rect rect, Rect parent)
        {
            Assert.True(rect.Width > 0 && rect.Height > 0, $"Empty bounds: {rect}");
            Assert.True(rect.Left >= parent.Left && rect.Top >= parent.Top &&
                rect.Right <= parent.Right && rect.Bottom <= parent.Bottom, $"{rect} is outside {parent}");
        }
    }

    public class Context : DispatchProxy
    {
        public IPluginStorage Storage = null!;
        public Func<Frame, Task<bool>>? Clipboard;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Storage" => Storage,
            "get_Frames" => new EmptyFrames(),
            "CopyImageAsync" => Clipboard?.Invoke((Frame)args![0]!) ?? Task.FromResult(false),
            "Log" => null,
            _ => throw new NotSupportedException(method.Name)
        };
    }
    sealed class EmptyFrames : IFrameSource
    {
        public CaptureStatus Status => CaptureStatus.Stopped;
        public GameFrame? Latest => null;
        public event Action<CaptureStatus>? StatusChanged { add { } remove { } }
        public IDisposable Subscribe(Action<GameFrame> onFrame, double fps) => throw new NotSupportedException();
    }
    sealed class Storage(string folder) : IPluginStorage
    {
        readonly Dictionary<string, string> _values = new();
        public string Folder => folder;
        public T Load<T>(string name) where T : new() => _values.TryGetValue(name, out var json)
            ? System.Text.Json.JsonSerializer.Deserialize<T>(json)!
            : new();
        public void Save<T>(string name, T value) => _values[name] = System.Text.Json.JsonSerializer.Serialize(value);
    }
    static (Window window, SaleCardPanel panel, SaleCardEngine engine) Open(Storage? storage = null, Func<Frame, Task<bool>>? clipboard = null)
    {
        var context = DispatchProxy.Create<IPluginContext, Context>();
        ((Context)(object)context).Storage = storage ?? new Storage(Path.Combine(Path.GetTempPath(), "MapleLite-ui-tests", Guid.NewGuid().ToString("N")));
        ((Context)(object)context).Clipboard = clipboard;
        var catalog = ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "GameData", "Items"));
        Assert.True(catalog.Count > 100);
        var engine = new SaleCardEngine(context) { Vision = new Vision(catalog) };
        engine.LoadSession();
        engine.S.Seller = "CuteLilSin"; engine.S.Channel = "30"; engine.S.MonitorOff = true; engine.S.SectionV = 2;
        string[] names = ["Green Bamboo Hat", "Blue Bamboo Hat", "Dark Guise", "Sapphire Earrings", "Meba", "Emerald Earrings", "Emerald Ore"];
        foreach (var name in names)
        {
            var cat = catalog.ByName(name) ?? catalog.Items.First(c => c.name.Contains(name, StringComparison.OrdinalIgnoreCase));
            var item = engine.AddByHand(cat);
            item.Tab = name is "Dark Guise" or "Sapphire Earrings" ? SaleCardEngine.StorageTab : "Equip";
            if (name == "Emerald Ore") item.Tab = "Etc";
            item.Include = name is "Emerald Earrings" or "Emerald Ore";
            item.Qty = name == "Emerald Ore" ? "20" : "1";
            item.Custom = name == "Emerald Ore" ? "800" : "14000";
        }
        var scrollCat = catalog.Items.First(c => c.id >= 2040000 && c.id < 2050000);
        var scroll = engine.AddByHand(scrollCat); scroll.Qty = "1"; scroll.Custom = "120000";
        var panel = new SaleCardPanel(engine, context);
        var window = new Window { Width = 1240, Height = 1040, Content = panel, Padding = new Thickness(16), Background = new SolidColorBrush(Color.Parse("#14171E")) };
        window.Show(); Pump();
        return (window, panel, engine);
    }
    static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    static IEnumerable<T> Visible<T>(Control root) where T : Control => root.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible);
    static void Click(Window window, Button button)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
    }

    static void Drag(Window window, GridSplitter splitter, Vector delta)
    {
        var point = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseMove(point + delta, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point + delta, MouseButton.Left);
        Pump();
    }

    [AvaloniaFact]
    public void Shop_card_dividers_resize_and_collapsed_sections_restore_the_saved_layout()
    {
        var storage = new Storage(Path.Combine(Path.GetTempPath(), "MapleLite-ui-tests", Guid.NewGuid().ToString("N")));
        var (window, panel, _) = Open(storage);
        GridSplitter Divider(SaleCardPanel page, string name) =>
            Visible<GridSplitter>(page).Single(s => AutomationProperties.GetName(s) == name);
        Button Header(SaleCardPanel page, string name) =>
            Visible<Button>(page).Single(b => AutomationProperties.GetName(b) == name);
        double x, y;
        try
        {
            var lists = Divider(panel, "Resize the item lists");
            var preview = Divider(panel, "Resize the item area and card preview");
            var initialLists = lists.TranslatePoint(default, panel)!.Value;
            var initialPreview = preview.TranslatePoint(default, panel)!.Value;
            Drag(window, lists, new Vector(80, 0));
            Drag(window, preview, new Vector(0, -60));
            lists.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
            Pump();
            x = lists.TranslatePoint(default, panel)!.Value.X;
            y = preview.TranslatePoint(default, panel)!.Value.Y;
            Assert.Equal(initialLists.X + 64, x, 1);
            Assert.Equal(initialPreview.Y - 60, y, 1);

            Click(window, Header(panel, "Collapse Available items"));
            Assert.DoesNotContain(Visible<TextBox>(panel), box => box.Watermark == "Search items…");
            Assert.DoesNotContain(Visible<GridSplitter>(panel), s => s == lists);
            Click(window, Header(panel, "Collapse On your card"));
            Assert.DoesNotContain(Visible<TextBox>(panel), box => box.Watermark == "Search selling card…");
            Click(window, Header(panel, "Collapse Card preview"));
            Assert.DoesNotContain(Visible<Button>(panel), b => Equals(b.Content, "Copy image"));
        }
        finally { window.Close(); }

        var (reopened, page, _) = Open(storage);
        try
        {
            Click(reopened, Header(page, "Expand Available items"));
            Click(reopened, Header(page, "Expand On your card"));
            Click(reopened, Header(page, "Expand Card preview"));
            var lists = Divider(page, "Resize the item lists");
            Assert.Equal(x, lists.TranslatePoint(default, page)!.Value.X, 1);
            Assert.Equal(y, Divider(page, "Resize the item area and card preview").TranslatePoint(default, page)!.Value.Y, 1);

            Click(reopened, Visible<Button>(page).Single(b => Equals(b.Content, "⛶  Enlarge")));
            Assert.Empty(Visible<GridSplitter>(page));
            Click(reopened, Visible<Button>(page).Single(b => Equals(b.Content, "⤡  Reduce")));
            Assert.Equal(y, Divider(page, "Resize the item area and card preview").TranslatePoint(default, page)!.Value.Y, 1);

            reopened.Width = 700; Pump();
            Assert.Equal(GridResizeDirection.Rows, lists.ResizeDirection);
            double initialY = lists.TranslatePoint(default, page)!.Value.Y;
            Drag(reopened, lists, new Vector(0, 24));
            Assert.Equal(initialY + 24, lists.TranslatePoint(default, page)!.Value.Y, 1);

            page.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; Pump();
            Assert.Contains(Visible<Button>(page), b => AutomationProperties.GetName(b) == "Collapse Item database");
            Assert.Equal(GridResizeDirection.Rows, Divider(page, "Resize the item lists").ResizeDirection);
            page.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 0; Pump();
            reopened.Width = 1240; Pump();
            Assert.Equal(x, lists.TranslatePoint(default, page)!.Value.X, 1);
        }
        finally { reopened.Close(); }
    }
    static void Capture(Window window, string name)
    {
        var panel = window.Content as SaleCardPanel;
        typeof(SaleCardPanel).GetMethod("RenderCard", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, null);
        Pump();
        Assert.Contains(Visible<Image>(panel!), image => image.Source != null && image.Bounds.Width > 100);
        var folder = Environment.GetEnvironmentVariable("MAPLELITE_UI_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame); frame.Save(Path.Combine(folder, name + ".png"));
    }

    [AvaloniaFact]
    public void Preview_price_adjustments_flyout_keeps_the_item_type_sliders_and_reset()
    {
        var (window, panel, engine) = Open();
        try
        {
            var button = Visible<Button>(panel).Single(button => Equals(button.Content, "Price adjustments…"));
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            button.BringIntoView();
            Pump();
            Click(window, button);
            Assert.True(flyout.IsOpen);
            var scroll = Assert.IsType<ScrollViewer>(flyout.Content);
            var kinds = Assert.IsType<StackPanel>(scroll.Content);
            var armor = Assert.Single(kinds.Children.OfType<StackPanel>(), row => row.Children.OfType<TextBlock>().Any(text => text.Text == "Armor"));
            Assert.Single(Visible<Slider>(armor)).Value = 96;
            Assert.Equal(96, engine.KindPercent("Armor"));
            Assert.Single(Visible<Button>(armor), reset => Equals(reset.Content, "96%"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(100, engine.KindPercent("Armor"));
            flyout.Hide();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Buying_items_get_their_own_total_on_the_card()
    {
        var (window, _, engine) = Open();
        try
        {
            var (_, before) = engine.Card();
            Assert.NotEqual("", before.Total);
            engine.SetBuying(engine.Vision!.Catalog.Values.First(i => i.name == "Emerald Ore"), true);
            var ore = engine.Items.First(i => i.Name == "Emerald Ore" && (i.Buying || i.IsWanted));
            ore.Custom = "1000";
            var (items, options) = engine.Card();
            Assert.Equal(before.Total, options.Total);           // buying never adds to the selling total
            Assert.NotEqual("", options.BuyTotal);
            var (withBuy, a) = CardRenderer.Render(items, options);
            var (withoutBuy, b) = CardRenderer.Render(items, options with { BuyTotal = "" });
            using (a) using (b) Assert.True(withBuy.H > withoutBuy.H, "the TOTAL BUYING block should add height");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Popout_price_slider_stays_in_sync_and_copy_renders_the_latest_adjustment()
    {
        Frame? copied = null;
        var (window, panel, engine) = Open(clipboard: frame => { copied = frame; return Task.FromResult(true); });
        try
        {
            engine.S.NoTime = true;
            Visible<Button>(panel).Single(button => Equals(button.Content, "Pop out ↗")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            var popout = Assert.IsType<CardPopout>(Assert.Single(window.OwnedWindows));
            Slider OverallSlider(Control root) => Visible<Slider>(root).Single(slider => AutomationProperties.GetName(slider) == "Overall card price adjustment");
            var mainSlider = OverallSlider(panel);
            var popoutSlider = OverallSlider(popout);
            mainSlider.Value = 92;
            Pump();
            Assert.Equal(92, popoutSlider.Value);
            popoutSlider.Value = 81;
            var copy = Visible<Button>(popout).Single(button => Equals(button.Content, "Copy image"));
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.Equal(81, mainSlider.Value);
            var (items, options) = engine.Card();
            Assert.Equal(0.81, options.PriceScale);
            var (frame, bitmap) = CardRenderer.Render(items, options);
            using (bitmap) Assert.Equal(frame.Px, Assert.IsType<Frame>(copied).Px);
            Assert.Equal("800", engine.Items.Single(item => item.Name == "Emerald Ore").Custom);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Popout_price_controls_follow_measured_tiles_and_edit_the_same_items()
    {
        var (window, panel, engine) = Open();
        try
        {
            Visible<Button>(panel).Single(b => Equals(b.Content, "Pop out ↗")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            var popout = Assert.IsType<CardPopout>(Assert.Single(window.OwnedWindows));
            Assert.Equal(WindowState.Maximized, popout.WindowState);
            var ore = engine.Items.Single(i => i.Name == "Emerald Ore");
            var click = Visible<Button>(popout).Single(b => AutomationProperties.GetName(b) == $"Edit base price for {ore.Name}");
            var location = click.TranslatePoint(default, popout)!.Value;
            Assert.True(location.X > 0 && location.Y > 0);
            Assert.True(click.Bounds.Width > 20);
            click.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            var field = Assert.Single(Visible<TextBox>(popout));
            Assert.Equal(ore.Custom, field.Text);
            field.Text = "4321";
            field.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Pump();
            Assert.Equal("4321", ore.Custom);
            Assert.Contains(Visible<TextBox>(panel), box => ReferenceEquals(box.DataContext, ore) && box.Text == "4321");
            Visible<Button>(popout).Single(b => AutomationProperties.GetName(b) == $"Remove {ore.Name} from the card")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.False(ore.Include);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Selected_filters_sort_live_edits_and_manual_order_share_saved_data()
    {
        var (window, panel, engine) = Open();
        try
        {
            int total = engine.Items.Count(i => i.Include);
            var filter = Visible<TextBox>(panel).Single(t => t.Watermark == "Search selling card…");
            filter.Text = "Emerald Ore"; Pump();
            Assert.Single(Visible<TextBox>(panel), t => t.Watermark == "qty");
            Assert.Equal(total, engine.Card().items.Count);
            filter.Text = ""; Pump();
            var sort = Visible<Button>(panel).Last(b => b.Content as string == "Name");
            Click(window, sort);
            var rows = Visible<TextBox>(panel).Where(t => t.Watermark == "qty").Select(t => (SaleItem)t.DataContext!).ToList();
            Assert.Equal(rows.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase), rows);
            var edit = Visible<ToggleButton>(panel).Single(b => b.Content as string == "Edit card");
            edit.IsChecked = true; Pump();
            var price = Visible<TextBox>(panel).First(t => t.Watermark == "Base price (blank = going rate)");
            var item = Assert.IsType<SaleItem>(price.DataContext);
            price.Text = "12345"; Pump();
            Assert.Equal("12345", item.Custom);
            Assert.Contains(Visible<TextBox>(panel), t => t.Watermark == "price" && ReferenceEquals(t.DataContext, item) && t.Text == "12345");
            var down = Visible<Button>(panel).First(b => b.Content as string == "↓" && b.IsEnabled);
            var moved = Assert.IsType<SaleItem>(down.DataContext);
            // Invoke the action even if the page's outer scroll puts the preview below the viewport.
            down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert.Equal("Manual", engine.S.Order);
            Assert.Equal(moved.Key, engine.Card().items[1].Key);
            Assert.False(engine.Card().options.Groups);
            engine.SaveSession();
            var saved = System.Text.Json.JsonSerializer.Deserialize<Session>(File.ReadAllText(Path.Combine(engine.DataFolder, "session.json")))!;
            Assert.Equal(engine.S.ManualOrder, saved.ManualOrder);
            Assert.Contains(saved.Items, i => i.Key == item.Key && i.Custom == "12345");
            Capture(window, "card-live-editor");
            window.Width = 700; Pump(); Capture(window, "card-live-editor-narrow");
            Assert.All(Visible<TextBox>(panel).Where(t => t.Watermark == "Item name"), input =>
                Assert.True(input.TranslatePoint(default, panel)!.Value.X + input.Bounds.Width <= panel.Bounds.Width));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Buying_card_filter_and_sort_do_not_change_selection()
    {
        var (window, panel, engine) = Open();
        try
        {
            engine.SetBuying(engine.Vision!.Catalog.Values.First(i => i.name == "Meba"), true);
            engine.SetBuying(engine.Vision.Catalog.Values.First(i => i.name == "Emerald Ore"), true);
            panel.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; Pump();
            var filter = Visible<TextBox>(panel).Single(t => t.Watermark == "Search buying card…");
            filter.Text = "Meba"; Pump();
            Assert.Single(Visible<TextBox>(panel), t => t.Watermark == "qty");
            Assert.Equal(2, engine.Items.Count(i => i.IsWanted));
            filter.Text = ""; Pump();
            Click(window, Visible<Button>(panel).Single(b => (b.Content as string)?.StartsWith("Name") == true));
            var names = Visible<TextBox>(panel).Where(t => t.Watermark == "qty").Select(t => ((SaleItem)t.DataContext!).Name).ToList();
            Assert.Equal(names.OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase), names);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Selling_add_remove_filters_and_editing_keep_card_membership_separate()
    {
        var (window, panel, engine) = Open();
        try
        {
            Capture(window, "selling");
            int count = engine.Items.Count(i => i.Include);
            Click(window, Visible<Button>(panel).Single(b => b.Content as string == "Sync"));
            var confirmation = window.OwnedWindows.Single();
            Assert.Contains(Visible<TextBlock>(confirmation), t => t.Text?.Contains("does not wipe your card") == true);
            Click(confirmation, Visible<Button>(confirmation).Single(b => b.Content as string == "Cancel"));
            Assert.Empty(window.OwnedWindows);
            Click(window, Visible<Button>(panel).Single(b => b.Content as string == "Sync"));
            confirmation = window.OwnedWindows.Single();
            Click(confirmation, Visible<Button>(confirmation).Single(b => b.Content as string == "Sync"));
            Assert.Equal("Capture is stopped.", engine.Status);
            Assert.Equal(count, engine.Items.Count(i => i.Include));
            var source = Visible<ComboBox>(panel).First(c => c.Items.Contains("All sources"));
            source.SelectedItem = "Storage"; Pump();
            Assert.All(Visible<Button>(panel).Where(b => b.Content as string == "+ Add"), b => Assert.Equal(SaleCardEngine.StorageTab, Assert.IsType<SaleItem>(b.DataContext).Tab));
            source.SelectedItem = "All sources"; Pump();
            var add = Visible<Button>(panel).First(b => b.Content as string == "+ Add");
            Click(window, add);
            Assert.Equal(count + 1, engine.Items.Count(i => i.Include));
            var search = Visible<TextBox>(panel).Single(t => t.Watermark == "Search items…");
            search.Text = "no-such-item-zz"; Pump();
            Assert.Equal(count + 1, engine.Items.Count(i => i.Include));
            Assert.DoesNotContain(Visible<Button>(panel), b => b.Content as string == "+ Add");
            Assert.Contains(Visible<TextBlock>(panel), t => t.Text == $"On your card · {count + 1}");
            var qty = Visible<TextBox>(panel).First(t => t.Watermark == "qty");
            var card = Assert.IsType<SaleItem>(qty.DataContext);
            qty.Text = "17"; Pump(); Assert.Equal("17", card.Qty);
            Click(window, Visible<Button>(panel).First(b => b.Content as string == "−"));
            Assert.Equal(count, engine.Items.Count(i => i.Include));
            search.Text = ""; Pump();
            Assert.Contains(Visible<Button>(panel), b => b.Content as string == "+ Add");
            window.Width = 700; Pump(); Capture(window, "selling-narrow");
            var inputs = Visible<TextBox>(panel).Where(t => t.Watermark == "qty").ToList();
            Assert.All(inputs, input => Assert.True(input.TranslatePoint(default, panel)!.Value.X + input.Bounds.Width <= panel.Bounds.Width));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Buying_add_remove_and_wanted_details_are_independent_of_search()
    {
        var (window, panel, engine) = Open();
        try
        {
            var tabs = panel.GetVisualDescendants().OfType<TabControl>().Single(); tabs.SelectedIndex = 1; Pump();
            var search = Visible<TextBox>(panel).Single(t => t.Watermark == "Search the item database…");
            search.Text = "Meba"; Pump();
            Click(window, Visible<Button>(panel).First(b => b.Content as string == "+ Add"));
            var wanted = Assert.Single(engine.Items, i => i.IsWanted);
            search.Text = "scroll"; Pump();
            Assert.Single(engine.Items, i => i.IsWanted);
            var qty = Visible<TextBox>(panel).First(t => t.Watermark == "qty"); qty.Text = "2"; Pump();
            Assert.Equal("2", wanted.Qty);
            Click(window, Visible<Button>(panel).First(b => b.Content as string == "▾"));
            Assert.Contains(Visible<TextBlock>(panel), t => t.Text?.StartsWith("What you want") == true);
            Visible<TextBox>(panel).Single(t => t.Watermark == "+5").Text = "10";
            Click(window, Visible<Button>(panel).Single(b => b.Content as string == "Add"));
            Assert.Contains("Weapon Attack: +10 or more", wanted.Stats);
            Visible<TextBox>(panel).Single(t => t.Watermark == "price").Text = "25000"; Pump();
            Assert.Equal("25000", wanted.Custom);
            Capture(window, "buying");
            Click(window, Visible<Button>(panel).First(b => b.Content as string == "−"));
            Assert.DoesNotContain(engine.Items, i => i.IsWanted);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void Card_wide_tooltip_warning_switch_sits_beside_prices_and_preserves_recorded_details()
    {
        var (window, panel, engine) = Open();
        try
        {
            var prices = Visible<ToggleSwitch>(panel).Single(control => Equals(control.OffContent, "Prices hidden"));
            var warnings = Visible<ToggleSwitch>(panel).Single(control => Equals(control.OffContent, "Tooltip warnings hidden"));
            var toolbar = Assert.IsType<WrapPanel>(prices.Parent);
            Assert.Same(warnings, toolbar.Children[toolbar.Children.IndexOf(prices) + 1]);
            Assert.True(warnings.IsChecked);

            warnings.IsChecked = false;
            var (_, hidden) = engine.Card();
            Assert.True(hidden.HideMissingTooltipWarnings);
            Assert.True(hidden.Stats);
            var item = new SaleItem { ItemId = 1032009, Tab = "Equip", Name = "Earrings", Stats = "Magic Def: +30" };
            Assert.Equal("Magic Def: +30", item.CardDetails(hidden.HideMissingTooltipWarnings));
            Assert.True(item.MissingStats);

            warnings.IsChecked = true;
            var (_, shown) = engine.Card();
            Assert.False(shown.HideMissingTooltipWarnings);
            Assert.Contains("Slots remaining: not recorded", item.CardDetails(shown.HideMissingTooltipWarnings));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Card_category_picker_hides_selling_and_buying_tiles_and_remembers_the_choice()
    {
        var storage = new Storage(Path.Combine(Path.GetTempPath(), "MapleLite-ui-tests", Guid.NewGuid().ToString("N")));
        CheckBox GemsPicker(Window window, SaleCardPanel panel)
        {
            var button = Visible<Button>(panel).Single(b => Equals(b.Content, "Categories…"));
            Click(window, button);
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            Assert.True(flyout.IsOpen);
            var scroll = Assert.IsType<ScrollViewer>(flyout.Content);
            var categories = Assert.IsType<StackPanel>(scroll.Content);
            return categories.Children.OfType<CheckBox>().Single(box => Equals(box.Content, "Gems & ores"));
        }

        var (window, panel, engine) = Open(storage);
        try
        {
            engine.SetBuying(engine.Vision!.Catalog.Values.First(i => i.name == "Emerald Ore"), true);
            var (shown, options) = engine.Card();
            Assert.Equal(2, shown.Count(i => i.Category == "Gems & ores"));
            Assert.Equal("150k", options.Total);

            var gems = GemsPicker(window, panel);
            Assert.True(gems.IsChecked);
            gems.IsChecked = false;
            var (hidden, filteredOptions) = engine.Card();
            Assert.Equal(shown.Where(i => i.Category != "Gems & ores").Select(i => i.Key), hidden.Select(i => i.Key));
            Assert.Equal("134k", filteredOptions.Total);
            Assert.DoesNotContain(engine.EditableCardItems(), i => i.Category == "Gems & ores");
            Assert.Equal(2, engine.Items.Count(i => i.Include && i.Category == "Gems & ores"));
            engine.SaveSession();
        }
        finally { window.Close(); }

        var (reopened, restoredPanel, restoredEngine) = Open(storage);
        try
        {
            var gems = GemsPicker(reopened, restoredPanel);
            Assert.False(gems.IsChecked);
            Assert.DoesNotContain(restoredEngine.Card().items, i => i.Category == "Gems & ores");
            gems.IsChecked = true;
            Assert.Equal(2, restoredEngine.Card().items.Count(i => i.Category == "Gems & ores"));
        }
        finally { reopened.Close(); }
    }

    [AvaloniaFact]
    public void Missing_stats_notice_filters_and_clears_when_stats_are_entered()
    {
        var (window, panel, engine) = Open();
        try
        {
            var missing = engine.Items.Where(i => i.Include && i.MissingStats).ToList();
            Assert.NotEmpty(missing);
            Click(window, Visible<Button>(panel).Single(b => (b.Content as string)?.StartsWith("Missing stats") == true));
            Assert.All(Visible<TextBox>(panel).Where(b => b.Watermark == "qty"), b => Assert.True(Assert.IsType<SaleItem>(b.DataContext).MissingStats));
            Assert.Contains(Visible<Button>(panel), b => b.Content as string == "Read stats (5s)");
            Assert.Contains(Visible<Button>(panel), b => b.Content as string == "Enter stats");
            Capture(window, "missing-stats");
            var hide = Visible<Button>(panel).First(b => b.Content as string == "Hide warning");
            var hidden = Assert.IsType<SaleItem>(hide.DataContext);
            Click(window, hide);
            Assert.True(hidden.HideMissingTooltip);
            Assert.DoesNotContain("Tooltip not recorded", hidden.DetailsText);
            foreach (var item in missing) { item.Stats = "Magic Def: +30"; item.Slots = "5"; }
            Pump();
            Assert.Contains(Visible<TextBlock>(panel), t => t.Text == "No missing stats to review.");
        }
        finally { window.Close(); }
    }

}

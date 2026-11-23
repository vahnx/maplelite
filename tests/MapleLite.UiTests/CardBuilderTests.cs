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

    public class Context : DispatchProxy
    {
        public IPluginStorage Storage = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Storage" => Storage,
            "get_Frames" => new EmptyFrames(),
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
        public string Folder => folder;
        public T Load<T>(string name) where T : new() => new();
        public void Save<T>(string name, T value) { }
    }
    static (Window window, SaleCardPanel panel, SaleCardEngine engine) Open()
    {
        var context = DispatchProxy.Create<IPluginContext, Context>();
        ((Context)(object)context).Storage = new Storage(Path.Combine(Path.GetTempPath(), "MapleLite-ui-tests", Guid.NewGuid().ToString("N")));
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
            foreach (var item in missing) { item.Stats = "Magic Def: +30"; item.Slots = "5"; }
            Pump();
            Assert.Contains(Visible<TextBlock>(panel), t => t.Text == "All selected items have recorded stats.");
        }
        finally { window.Close(); }
    }

}

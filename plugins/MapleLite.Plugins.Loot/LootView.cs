using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.Loot;

/// <summary>
/// One tile per item with the total quantity, mesos first, for the chosen period (moved from Chat History's
/// Loot tab, which came from the MapleMemory app). Stack colors follow the theme: below 100K the warning
/// color, from 100K normal text, from 10M the success color.
/// </summary>
internal sealed class LootView : UserControl
{
    readonly LootLog _log;
    readonly ItemNames _names;
    readonly LootHighlights _highlights;
    readonly DateTimeOffset _sessionStart;
    internal static readonly string[] Periods = ["This session", "Today", "Yesterday", "Last 7 days", "Last 30 days", "All time", "Custom dates"];
    const int Session = 0, AllTime = 5, Custom = 6;
    readonly ComboBox _period = new() { Width = 160, ItemsSource = Periods, SelectedIndex = Session };
    readonly CalendarDatePicker _fromDate = new() { Width = 140, Watermark = "Any day" }, _toDate = new() { Width = 140, Watermark = "Any day" };
    readonly ComboBox _mapPick = new() { MinWidth = 220, MaxWidth = 420 };
    readonly TextBlock _totals = Muted("");
    readonly WrapPanel _tiles = new();
    readonly ListBox _recent = new() { Height = 220 };
    int _shownCount = -1, _shownHighlightRevision;
    string? _shownMap, _shownRange;
    bool _filling;
    const string AllMaps = "All maps", NoMap = "Map not recorded";

    public LootView(LootLog log, ItemNames names, DateTimeOffset sessionStart, LootHighlights highlights)
    {
        _log = log; _names = names; _sessionStart = sessionStart;
        _highlights = highlights;
        _period.SelectionChanged += (_, _) => { if (_filling) return; ShowDates(); Refresh(); };
        _fromDate.SelectedDateChanged += (_, _) => DatePicked();
        _toDate.SelectedDateChanged += (_, _) => DatePicked();
        _mapPick.SelectionChanged += (_, _) => { if (!_filling) Refresh(); };
        ToolTip.SetTip(_fromDate, "The first day to count. Pick a day to see any range, not just the presets.");
        ToolTip.SetTip(_toDate, "The last day to count (the whole day is included). Leave empty to count up to now.");
        ToolTip.SetTip(_mapPick, "Only the pickups made on one map. The map is read from the minimap, so keep its name showing (full size or collapsed).");
        ShowDates();
        Content = Stack(10, Wrap(_period, Muted("from"), _fromDate, Muted("to"), _toDate, _mapPick), _totals,
            Card(new ScrollViewer { Content = _tiles, MaxHeight = 440 }, 10),
            Title("Recent pickups"), Card(_recent, 4));
    }

    /// <summary>The days a preset covers, shown in the date pickers (all time: none).</summary>
    internal static (DateTime? From, DateTime? To) PresetDays(int period, DateTime today, DateTimeOffset sessionStart) => period switch
    {
        Session => (sessionStart.LocalDateTime.Date, today),
        1 => (today, today),
        2 => (today.AddDays(-1), today.AddDays(-1)),
        3 => (today.AddDays(-6), today),
        4 => (today.AddDays(-29), today),
        _ => (null, null),
    };

    /// <summary>
    /// The times counted: from the start of the first day to the end of the last, either end open when its day is empty.
    /// This session starts when MapleLite did, not at midnight.
    /// </summary>
    internal static (DateTimeOffset From, DateTimeOffset To) Window(int period, DateTimeOffset sessionStart, DateTime? from, DateTime? to)
    {
        var start = period == Session ? sessionStart : from is { } f ? new DateTimeOffset(f.Date) : DateTimeOffset.MinValue;
        var end = to is { } t ? new DateTimeOffset(t.Date.AddDays(1)) : DateTimeOffset.MaxValue;
        return (start, end);
    }

    void ShowDates()
    {
        if (_period.SelectedIndex == Custom) return; // keep what was picked
        var (f, t) = PresetDays(_period.SelectedIndex, DateTime.Today, _sessionStart);
        _filling = true;
        try { _fromDate.SelectedDate = f; _toDate.SelectedDate = t; }
        finally { _filling = false; }
    }

    void DatePicked()
    {
        if (_filling) return;
        // The pickers also report a change when they first show their dates: only a real change makes it "Custom dates".
        if (_period.SelectedIndex != Custom && PresetDays(_period.SelectedIndex, DateTime.Today, _sessionStart) is var (f0, t0)
            && f0?.Date == _fromDate.SelectedDate?.Date && t0?.Date == _toDate.SelectedDate?.Date) return;
        _filling = true;
        try { _period.SelectedIndex = Custom; }
        finally { _filling = false; }
        Refresh();
    }

    public void Refresh()
    {
        // A new day moves the presets along ("Today" at midnight).
        if (_period.SelectedIndex != Custom && PresetDays(_period.SelectedIndex, DateTime.Today, _sessionStart) is var (f0, t0)
            && (f0 != _fromDate.SelectedDate?.Date || t0 != _toDate.SelectedDate?.Date)) ShowDates();

        // Rebuild only when something changed: new pickups, other dates, or another map.
        var records = _log.Records;
        string pick = _mapPick.SelectedItem as string ?? AllMaps;
        var (from, to) = Window(_period.SelectedIndex, _sessionStart, _fromDate.SelectedDate, _toDate.SelectedDate);
        string range = $"{from:O}|{to:O}";
        if (records.Count == _shownCount && range == _shownRange && pick == _shownMap &&
            _shownHighlightRevision == _highlights.Revision) return;
        _shownCount = records.Count; _shownRange = range;
        _shownHighlightRevision = _highlights.Revision;

        var inPeriod = records.Where(r => r.Time >= from && r.Time < to).ToList();
        _shownMap = pick = FillMaps(inPeriod, pick);
        var drops = pick == AllMaps ? inPeriod : inPeriod.Where(r => (r.Map ?? NoMap) == pick).ToList();
        long gained = drops.Where(d => d.Kind == "Mesos").Sum(d => d.Quantity);
        long lost = drops.Where(d => d.Kind == "MesosLost").Sum(d => d.Quantity);
        var items = Totals(drops);

        _totals.Text = drops.Count == 0
            ? _fromDate.SelectedDate > _toDate.SelectedDate ? "The first day is after the last day." : "No pickups recorded for this period yet."
            : $"{drops.Count:N0} pickups  ·  {items.Count:N0} different items  ·  {gained:N0} mesos gained" + (lost > 0 ? $"  ·  {lost:N0} mesos lost" : "");

        _tiles.Children.Clear();
        if (gained > 0) _tiles.Children.Add(Tile("Mesos", gained, $"{gained:N0} mesos gained in {drops.Count(d => d.Kind == "Mesos"):N0} pickups", ThemeTokens.Accent, Picture("Mesos", "Mesos", 32)));
        foreach (var item in items)
            _tiles.Children.Add(Tile(item.Name, item.Quantity, $"{item.Name} ×{item.Quantity:N0} in {item.Pickups:N0} pickups. Last: {item.Last.ToLocalTime():MMM dd HH:mm:ss}",
                ThemeTokens.Text, Picture("Item", item.Name, 32), _highlights.Find("Item", item.Name)));

        _recent.ItemsSource = drops.AsEnumerable().Reverse().Take(200).Select(d =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Picture(d.Kind, d.Name, 24));
            var highlight = _highlights.Find(d.Kind, d.Name);
            var description = $"{d.When}   {d.Drop.Summary}   ({d.Source}" + (d.Map != null && pick == AllMaps ? $" · {d.Map})" : ")");
            var text = PanelKit.Text(highlight?.WithPrice(description) ?? description, ThemeTokens.Text, 12);
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            return highlight == null ? (Control)row : new Border
            {
                BorderBrush = new SolidColorBrush(highlight.Color), BorderThickness = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(5, 0, 0, 0), Child = row,
            };
        }).ToList();
    }

    /// <summary>
    /// Lists the maps pickups were made on in the period, most pickups first, and keeps <paramref name="pick"/> chosen
    /// when it's still there (else all maps). Returns what's chosen.
    /// </summary>
    string FillMaps(List<LootRecord> inPeriod, string pick)
    {
        var maps = new List<string> { AllMaps };
        maps.AddRange(inPeriod.GroupBy(r => r.Map ?? NoMap).OrderBy(g => g.Key == NoMap).ThenByDescending(g => g.Count()).Select(g => g.Key));
        if (!maps.Contains(pick)) pick = AllMaps;
        _filling = true;
        try
        {
            if (_mapPick.ItemsSource is not List<string> old || !old.SequenceEqual(maps)) _mapPick.ItemsSource = maps;
            _mapPick.SelectedItem = pick;
        }
        finally { _filling = false; }
        return pick;
    }

    readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The item's catalog picture at <paramref name="size"/> pixels (pixel art, so scaled without smoothing), a coin for
    /// mesos, or a themed question mark when the item picture is unavailable.
    /// </summary>
    Control Picture(string kind, string name, double size)
    {
        if (kind is "Mesos" or "MesosLost")
            return new Avalonia.Controls.Shapes.Ellipse
            {
                Width = size * 0.6, Height = size * 0.6, Margin = new Thickness(size * 0.2), StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x3A)), Stroke = new SolidColorBrush(Color.FromRgb(0xA8, 0x74, 0x10)),
            };
        if (!_icons.TryGetValue(name, out var bmp))
        {
            var path = _names.IconPath(name);
            try { bmp = path != null ? new Bitmap(path) : null; }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException) { bmp = null; }
            _icons[name] = bmp;
        }
        if (bmp == null)
        {
            var mark = PanelKit.Text("?", ThemeTokens.Text, size * 0.67, FontWeight.Bold);
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.VerticalAlignment = VerticalAlignment.Center;
            var outline = new Rectangle
            {
                RadiusX = size / 6, RadiusY = size / 6,
                StrokeThickness = 1, StrokeDashArray = new AvaloniaList<double> { 3, 2 },
            }.Themed(Shape.StrokeProperty, ThemeTokens.TextMuted);
            return new Grid { Width = size, Height = size, Children = { outline, mark } };
        }
        var image = new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        return image;
    }

    /// <summary>Items grouped by name (case-insensitive), most recently picked up first.</summary>
    internal static List<(string Name, long Quantity, int Pickups, DateTimeOffset Last)> Totals(IEnumerable<LootRecord> drops) =>
        drops.Where(d => d.Kind == "Item")
             .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
             .Select(g => (Name: g.First().Name, Quantity: g.Sum(d => d.Quantity), Pickups: g.Count(), Last: g.Max(d => d.Time)))
             .OrderByDescending(i => i.Last).ToList();

    internal static (string Text, string Token) StackLabel(long quantity) =>
        quantity >= 10_000_000 ? ($"{quantity / 1_000_000}M", ThemeTokens.Success)
        : quantity >= 100_000 ? ($"{quantity / 1_000}K", ThemeTokens.Text)
        : (quantity.ToString("N0"), ThemeTokens.Warning);

    static Border Tile(string name, long quantity, string tip, string nameToken, Control picture, LootHighlight? highlight = null)
    {
        var (text, token) = StackLabel(quantity);
        var count = PanelKit.Text(text, token, 13, FontWeight.Bold);
        count.Margin = new Thickness(6, 3, 0, 0); count.HorizontalAlignment = HorizontalAlignment.Left; count.VerticalAlignment = VerticalAlignment.Top;
        var label = PanelKit.Text(name, nameToken, 11);
        label.TextAlignment = TextAlignment.Center; label.VerticalAlignment = VerticalAlignment.Bottom; label.MaxHeight = 32;
        label.TextTrimming = TextTrimming.CharacterEllipsis; label.Margin = new Thickness(4, 0, 4, 4);
        picture.HorizontalAlignment = HorizontalAlignment.Center; picture.VerticalAlignment = VerticalAlignment.Top;
        picture.Margin = new Thickness(0, 8, 0, 0);
        var grid = new Grid();
        grid.Children.Add(picture); grid.Children.Add(count); grid.Children.Add(label);
        var tile = new Border { Width = 124, Height = 84, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = grid }
            .Themed(Border.BackgroundProperty, ThemeTokens.SurfaceAlt);
        if (highlight == null) tile.Themed(Border.BorderBrushProperty, ThemeTokens.Border);
        else
        {
            tile.BorderBrush = new SolidColorBrush(highlight.Color);
            tile.BorderThickness = new Thickness(2);
            if (highlight.PriceLabel is { } price) tip += $"\nArchived price: {price}";
        }
        ToolTip.SetTip(tile, tip);
        return tile;
    }
}

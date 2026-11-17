using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
    readonly DateTimeOffset _sessionStart;
    readonly ComboBox _period = new() { Width = 180, ItemsSource = new[] { "This session", "Today", "Last 7 days", "All time" }, SelectedIndex = 0 };
    readonly TextBlock _totals = Muted("");
    readonly WrapPanel _tiles = new();
    readonly ListBox _recent = new() { Height = 220 };
    int _shownCount = -1, _shownPeriod = -1;
    DateTime _shownDay;

    public LootView(LootLog log, DateTimeOffset sessionStart)
    {
        _log = log; _sessionStart = sessionStart;
        _period.SelectionChanged += (_, _) => Refresh();
        Content = Stack(10, _period, _totals,
            Card(new ScrollViewer { Content = _tiles, MaxHeight = 440 }, 10),
            Title("Recent pickups"), Card(_recent, 4));
    }

    DateTimeOffset From() => _period.SelectedIndex switch
    {
        0 => _sessionStart,
        1 => new DateTimeOffset(DateTime.Today),
        2 => DateTimeOffset.Now.AddDays(-7),
        _ => DateTimeOffset.MinValue,
    };

    public void Refresh()
    {
        // Rebuild only when something changed: new pickups, another period, or a new day.
        var records = _log.Records;
        if (records.Count == _shownCount && _period.SelectedIndex == _shownPeriod && DateTime.Today == _shownDay) return;
        _shownCount = records.Count; _shownPeriod = _period.SelectedIndex; _shownDay = DateTime.Today;

        var from = From();
        var drops = records.Where(r => r.Time >= from).ToList();
        long gained = drops.Where(d => d.Kind == "Mesos").Sum(d => d.Quantity);
        long lost = drops.Where(d => d.Kind == "MesosLost").Sum(d => d.Quantity);
        var items = Totals(drops);

        _totals.Text = drops.Count == 0
            ? "No pickups recorded for this period yet."
            : $"{drops.Count:N0} pickups  ·  {items.Count:N0} different items  ·  {gained:N0} mesos gained" + (lost > 0 ? $"  ·  {lost:N0} mesos lost" : "");

        _tiles.Children.Clear();
        if (gained > 0) _tiles.Children.Add(Tile("Mesos", gained, $"{gained:N0} mesos gained in {drops.Count(d => d.Kind == "Mesos"):N0} pickups", ThemeTokens.Accent));
        foreach (var item in items)
            _tiles.Children.Add(Tile(item.Name, item.Quantity, $"{item.Name} ×{item.Quantity:N0} in {item.Pickups:N0} pickups. Last: {item.Last.ToLocalTime():MMM dd HH:mm:ss}", ThemeTokens.Text));

        _recent.ItemsSource = drops.AsEnumerable().Reverse().Take(200).Select(d => $"{d.When}   {d.Drop.Summary}   ({d.Source})").ToList();
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

    static Border Tile(string name, long quantity, string tip, string nameToken)
    {
        var (text, token) = StackLabel(quantity);
        var count = PanelKit.Text(text, token, 13, FontWeight.Bold);
        count.Margin = new Thickness(6, 3, 0, 0); count.HorizontalAlignment = HorizontalAlignment.Left; count.VerticalAlignment = VerticalAlignment.Top;
        var label = PanelKit.Text(name, nameToken, 11);
        label.TextAlignment = TextAlignment.Center; label.VerticalAlignment = VerticalAlignment.Bottom; label.MaxHeight = 32;
        label.TextTrimming = TextTrimming.CharacterEllipsis; label.Margin = new Thickness(4, 0, 4, 4);
        var grid = new Grid();
        grid.Children.Add(count); grid.Children.Add(label);
        var tile = new Border { Width = 124, Height = 66, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = grid }
            .Themed(Border.BackgroundProperty, ThemeTokens.SurfaceAlt).Themed(Border.BorderBrushProperty, ThemeTokens.Border);
        ToolTip.SetTip(tile, tip);
        return tile;
    }
}

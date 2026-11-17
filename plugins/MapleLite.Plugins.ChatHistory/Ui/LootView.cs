using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.ChatHistory;

/// <summary>
/// Loot tracker: one tile per item with the total quantity, mesos first, for the chosen period.
/// Reads the saved journal (every pickup is also a Loot row), so history is never capped.
/// Stack colors follow the theme: below 100K warning color, from 100K normal text, from 10M success color.
/// </summary>
internal sealed class LootView : UserControl
{
    readonly MemoryEngine _engine;
    readonly ComboBox _period = new() { Width = 180, ItemsSource = new[] { "This session", "Today", "Last 7 days", "All time" }, SelectedIndex = 0 };
    readonly TextBlock _totals = Muted("");
    readonly WrapPanel _tiles = new();
    readonly ListBox _recent = new() { Height = 200 };
    int _shownCount = -1, _shownPeriod = -1;
    string _shownZone = "";

    public LootView(MemoryEngine engine)
    {
        _engine = engine;
        _period.SelectionChanged += (_, _) => Refresh();
        Content = Stack(10, _period, _totals,
            new ScrollViewer { Content = _tiles, MaxHeight = 420 },
            Muted("Recent pickups"), Card(_recent, 4));
    }

    DateTimeOffset From()
    {
        var now = ClockZone.Convert(DateTimeOffset.Now);
        return _period.SelectedIndex switch
        {
            0 => _engine.SessionStart,
            1 => new DateTimeOffset(now.Date, now.Offset),
            2 => DateTimeOffset.Now.AddDays(-7),
            _ => DateTimeOffset.MinValue,
        };
    }

    public void Refresh()
    {
        // Rebuild only when something changed: new entries, another period, or another time zone.
        var entries = _engine.Journal.Entries;
        if (entries.Count == _shownCount && _period.SelectedIndex == _shownPeriod && ClockZone.Zone.Id == _shownZone) return;
        _shownCount = entries.Count; _shownPeriod = _period.SelectedIndex; _shownZone = ClockZone.Zone.Id;

        var from = From();
        var drops = entries.Where(e => e.Channel == "Loot" && e.Time >= from)
            .Select(e => (Entry: e, Drop: LootParser.Parse(e.Text))).Where(d => d.Drop != null).ToList();
        long gained = drops.Where(d => d.Drop!.Kind == "Mesos").Sum(d => d.Drop!.Quantity);
        long lost = drops.Where(d => d.Drop!.Kind == "MesosLost").Sum(d => d.Drop!.Quantity);
        var items = drops.Where(d => d.Drop!.Kind == "Item")
            .GroupBy(d => d.Drop!.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Drop!.Name, Quantity: g.Sum(d => d.Drop!.Quantity), Pickups: g.Count(), Last: g.Max(d => d.Entry.Time)))
            .OrderByDescending(i => i.Last).ToList();

        _totals.Text = drops.Count == 0
            ? "No pickups recorded for this period yet."
            : $"{drops.Count:N0} pickups  ·  {items.Count:N0} different items  ·  {gained:N0} mesos gained" + (lost > 0 ? $"  ·  {lost:N0} mesos lost" : "");

        _tiles.Children.Clear();
        if (gained > 0) _tiles.Children.Add(Tile("Mesos", gained, $"{gained:N0} mesos gained in {drops.Count(d => d.Drop!.Kind == "Mesos"):N0} pickups", ThemeTokens.Accent));
        foreach (var item in items)
            _tiles.Children.Add(Tile(item.Name, item.Quantity, $"{item.Name} ×{item.Quantity:N0} in {item.Pickups:N0} pickups. Last: {ClockZone.Convert(item.Last):MMM dd HH:mm:ss}", ThemeTokens.Text));

        _recent.ItemsSource = drops.AsEnumerable().Reverse().Take(200).Select(d => $"{d.Entry.When}   {d.Drop!.Summary}").ToList();
    }

    internal static (string Text, string Token) StackLabel(long quantity) =>
        quantity >= 10_000_000 ? ($"{quantity / 1_000_000}M", ThemeTokens.Success)
        : quantity >= 100_000 ? ($"{quantity / 1_000}K", ThemeTokens.Text)
        : (quantity.ToString("N0"), ThemeTokens.Warning);

    static Border Tile(string name, long quantity, string tip, string nameToken)
    {
        var (text, token) = StackLabel(quantity);
        var grid = new Grid();
        grid.Children.Add(PanelKit.Text(text, token, 13, FontWeight.Bold).With(t => { t.Margin = new Thickness(6, 3, 0, 0); t.HorizontalAlignment = HorizontalAlignment.Left; t.VerticalAlignment = VerticalAlignment.Top; }));
        grid.Children.Add(PanelKit.Text(name, nameToken, 11).With(t =>
        {
            t.TextAlignment = TextAlignment.Center; t.VerticalAlignment = VerticalAlignment.Bottom; t.MaxHeight = 32;
            t.TextTrimming = TextTrimming.CharacterEllipsis; t.Margin = new Thickness(4, 0, 4, 4);
        }));
        var tile = new Border { Width = 124, Height = 66, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = grid }
            .Themed(Border.BackgroundProperty, ThemeTokens.SurfaceAlt).Themed(Border.BorderBrushProperty, ThemeTokens.Border);
        ToolTip.SetTip(tile, tip);
        return tile;
    }
}

internal static class ControlExtensions
{
    public static T With<T>(this T c, Action<T> set) { set(c); return c; }
}

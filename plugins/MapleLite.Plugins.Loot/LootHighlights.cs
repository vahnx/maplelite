using Avalonia.Media;
using MapleLite.Core.Game;

namespace MapleLite.Plugins.Loot;

public sealed record LootPriceRange(long? Min, long? Max, string Color)
{
    public bool Matches(long price) =>
        (Min.HasValue || Max.HasValue) && price > 0 && price >= (Min ?? 0) && price <= (Max ?? long.MaxValue);

    internal static List<LootPriceRange> Defaults() =>
    [
        new(null, null, "#3DDC97"),
        new(null, null, "#56AAFF"),
        new(null, null, "#B891FF"),
        new(null, null, "#FFB54D"),
        new(null, null, "#FF6D91"),
    ];
}

public sealed record LootItemHighlight(string Name, string Color);

internal sealed record LootHighlight(Color Color, long? UnitPrice)
{
    public string? PriceLabel => UnitPrice is { } price ? $"{price:N0} mesos each" : null;
    public string WithPrice(string text) => PriceLabel is { } price ? $"{text} · {price}" : text;
    public uint Rgb => ((uint)Color.R << 16) | ((uint)Color.G << 8) | Color.B;
}

internal sealed class LootHighlights(ItemCatalog items)
{
    readonly Dictionary<string, int> _ids = items.Items.GroupBy(i => i.name, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() == 1)
        .ToDictionary(g => g.Key, g => g.First().id, StringComparer.OrdinalIgnoreCase);

    IReadOnlyDictionary<int, long> _prices = new Dictionary<int, long>();
    readonly Dictionary<string, Color> _itemColors = new(StringComparer.OrdinalIgnoreCase);
    (LootPriceRange Range, Color Color)[] _rules = [];
    public int Revision { get; private set; }
    public bool HasPriceRanges => _rules.Length > 0;

    public void SetRules(IEnumerable<LootPriceRange> ranges, IEnumerable<LootItemHighlight> items)
    {
        var rules = new List<(LootPriceRange, Color)>();
        foreach (var range in ranges)
            if ((range.Min.HasValue || range.Max.HasValue) && Color.TryParse(range.Color, out var color))
                rules.Add((range, Color.FromRgb(color.R, color.G, color.B)));
        _rules = rules.ToArray();
        _itemColors.Clear();
        foreach (var item in items)
            if (!string.IsNullOrWhiteSpace(item.Name) && Color.TryParse(item.Color, out var color))
                _itemColors[item.Name] = Color.FromRgb(color.R, color.G, color.B);
        Revision++;
    }

    public bool SetPrices(IReadOnlyDictionary<int, long> prices)
    {
        if (_prices.Count == prices.Count && _prices.All(p => prices.TryGetValue(p.Key, out long value) && value == p.Value))
            return false;
        _prices = prices;
        Revision++;
        return true;
    }

    public LootHighlight? Find(string kind, string name)
    {
        if (kind != "Item") return null;
        long? price = HasPriceRanges && _ids.TryGetValue(name, out int id) && _prices.TryGetValue(id, out long estimate)
            ? estimate : null;
        if (_itemColors.TryGetValue(name, out var selectedColor)) return new(selectedColor, price);
        if (price is not { } unitPrice) return null;
        foreach (var (range, color) in _rules)
            if (range.Matches(unitPrice)) return new(color, unitPrice);
        return null;
    }
}

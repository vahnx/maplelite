using MapleLite.Core.Game;

namespace MapleLite.Plugins.SaleCard;

/// <summary>Browsing never changes which items are included on a card.</summary>
internal static class CardSelection
{
    public static string SourceOf(SaleItem item) => item.IsAdded ? "Manual"
        : item.Tab == SaleCardEngine.EquippedTab ? "Equipped"
        : item.InShop ? "My FM shop" : item.Tab == SaleCardEngine.StorageTab ? "Storage" : "Inventory";

    public static bool Available(SaleItem item, string? search, string? source, string? type) =>
        !item.IsWanted && !item.Include && Matches(item.Name, search)
        && (string.IsNullOrEmpty(source) || source == "All sources" || SourceOf(item) == source)
        && (string.IsNullOrEmpty(type) || type == "All types" || item.Category == type);

    public static bool Selected(SaleItem item, bool buying, string? search, string? source, string? type) =>
        item.IsWanted == buying && item.Include && Matches(item.Name, search)
        && (buying || string.IsNullOrEmpty(source) || source == "All sources" || SourceOf(item) == source)
        && (string.IsNullOrEmpty(type) || type == "All types" || item.Category == type);

    public static bool Matches(string name, string? search) => (search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .All(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<CatalogItem> CatalogMatches(IEnumerable<CatalogItem> catalog, string? search, string? type, ISet<int> selected) =>
        catalog.Where(item => !selected.Contains(item.id) && Matches(item.name, search)
            && (string.IsNullOrEmpty(type) || type == "All types" || Categories.Of(item.id) == type))
        .OrderBy(item => item.name.StartsWith((search ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(item => item.name, StringComparer.OrdinalIgnoreCase);
}

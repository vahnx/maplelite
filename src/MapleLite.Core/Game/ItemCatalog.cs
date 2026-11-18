using System.Text.Json;

namespace MapleLite.Core.Game;

/// <summary>
/// The MapleStory Classic item catalog every plugin shares: item ids, names and URL slugs (items.json) and each item's
/// inventory picture (icons/{id}.png). The host ships it as GameData/Items beside the app and hands it to plugins as
/// <c>context.Items</c>. Read-only; an empty catalog when the files are missing.
/// </summary>
public sealed class ItemCatalog
{
    readonly Dictionary<int, CatalogItem> _byId = new();
    readonly Dictionary<string, CatalogItem> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder name, under the app folder, the host ships the catalog in.</summary>
    public const string ShippedFolder = "GameData/Items";

    public static ItemCatalog Empty { get; } = new([], null);

    public ItemCatalog(IEnumerable<CatalogItem> items, string? iconFolder)
    {
        var list = new List<CatalogItem>();
        foreach (var it in items)
        {
            if (!_byId.TryAdd(it.id, it)) continue;
            _byName.TryAdd(it.name, it);
            list.Add(it);
        }
        Items = list;
        IconFolder = iconFolder;
    }

    /// <summary>Every item, in file order, each id once.</summary>
    public IReadOnlyList<CatalogItem> Items { get; }
    public int Count => Items.Count;
    /// <summary>The folder of icons/{id}.png, or null when there is none.</summary>
    public string? IconFolder { get; }

    public CatalogItem? ById(int id) => _byId.GetValueOrDefault(id);
    /// <summary>The item with exactly this name (ignoring case). Several items can share a name; this is the first.</summary>
    public CatalogItem? ByName(string name) => _byName.GetValueOrDefault(name);
    public string Name(int id) => ById(id)?.name ?? "";

    /// <summary>The item's picture, or null when it has none.</summary>
    public string? IconPath(int id)
    {
        if (IconFolder == null) return null;
        var path = Path.Combine(IconFolder, id + ".png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The picture of the item with exactly this name, or null.</summary>
    public string? IconPath(string name) => ByName(name) is { } it ? IconPath(it.id) : null;

    /// <summary>Loads items.json and icons/ from a folder. An empty catalog when it can't be read.</summary>
    public static ItemCatalog Load(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "items.json");
            if (!File.Exists(path)) return Empty;
            var items = JsonSerializer.Deserialize<List<CatalogItem>>(File.ReadAllText(path)) ?? [];
            var icons = Path.Combine(folder, "icons");
            return new ItemCatalog(items, Directory.Exists(icons) ? icons : null);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return Empty; }
    }

    /// <summary>The catalog the host ships beside the app (GameData/Items).</summary>
    public static ItemCatalog LoadShipped() => Load(Path.Combine(AppContext.BaseDirectory, ShippedFolder));
}

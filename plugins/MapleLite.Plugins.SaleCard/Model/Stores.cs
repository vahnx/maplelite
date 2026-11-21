using MapleLite.Core.Game;
using System.Text.Json;

namespace MapleLite.Plugins.SaleCard;

/// <summary>
/// Reads and writes the plugin's saved files so that one bad moment cannot lose them: a read waits out a file that is
/// busy, a write goes to a side file first and then takes the real one's place (keeping the previous one as .bak),
/// and a file that could not be read is set aside instead of being overwritten with an empty one.
/// Same behavior as the Sale Card Generator app.
/// </summary>
public static class Store
{
    static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase);

    public static string Read(string file)
    {
        for (int i = 0; ; i++)
        {
            try { return File.ReadAllText(file); }
            catch (IOException) when (i < 5) { Thread.Sleep(120); }
        }
    }

    /// <summary>Call when a file that exists could not be read or understood: keeps a copy of it.</summary>
    public static void Failed(string file)
    {
        try { if (File.Exists(file)) File.Copy(file, file + ".unreadable", true); }
        catch { lock (Blocked) Blocked.Add(file); }   // cannot even copy it: leave it alone for this run
    }

    /// <summary>Set while the plugin resets itself: nothing more is written.</summary>
    public static bool Frozen;

    public static void Write(string file, string text)
    {
        lock (Blocked) if (Frozen || Blocked.Contains(file)) return;
        try
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(file)) File.Copy(file, file + ".bak", true);
            File.Move(tmp, file, true);
        }
        catch { /* a failed save must never interrupt the plugin */ }
    }

    internal static T Load<T>(string file) where T : new()
    {
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<T>(Read(file)) ?? new T();
        }
        catch { Failed(file); }
        return new T();
    }
}

/// <summary>The data file names, unchanged from the Sale Card Generator app so its Data folder can be copied in.</summary>
public static class DataFiles
{
    public const string Session = "session.json", FmPrices = "fm-prices.json", SitePrices = "site-prices.json",
        PriceBook = "pricebook.json", HoverStats = "hover-stats.json", NotForSale = "not-for-sale.json";
    public static readonly string[] Prices = { PriceBook, FmPrices, SitePrices };
    public static readonly string[] Others = { Session, HoverStats, NotForSale };
    public static readonly string[] All = Prices.Concat(Others).ToArray();
}

/// <summary>Sorts items into filter groups from their item id.</summary>
public static class Categories
{
    public const string Unknown = "Unknown";

    public static readonly string[] Order =
    {
        "Weapons", "Armor", "Accessories", "Healing (HP/MP)", "Boost potions", "Upgrade scrolls", "Teleport scrolls", "Projectiles", "Other use",
        "Set-up", "Gems & ores", "Etc", "Cash", Unknown,
    };

    public static string Of(int id)
    {
        if (id <= 0) return Unknown;
        int sub = id / 10000;
        switch (id / 1000000)
        {
            case 1:
                if (sub >= 130 && sub <= 170) return "Weapons";
                if (sub is 101 or 102 or 103 or 111 or 112 or 113 or 114) return "Accessories";
                return "Armor";
            case 2:
                // 2002xxx and 2012xxx are the stat potions (Warrior, Sniper, Speed, Magic, Drake's Blood…);
                // the rest restore HP/MP or cure a status.
                if (id / 1000 is 2002 or 2012) return "Boost potions";
                if (sub is 200 or 201 or 202 or 205) return "Healing (HP/MP)";
                if (sub == 203) return "Teleport scrolls";   // return scrolls
                if (sub is 204 or 249) return "Upgrade scrolls";  // stat scrolls, clean slate / chaos scrolls
                if (sub is 206 or 207 or 233) return "Projectiles";
                return "Other use";
            case 3: return "Set-up";
            case 4:
                // mineral and jewel ores, refined plates and jewels, crystals
                return sub is 401 or 402 || id / 1000 is 4004 or 4005 ? "Gems & ores" : "Etc";
            case 5: return "Cash";
            default: return Unknown;
        }
    }
}

public static class Mesos
{
    public static string Format(long v)
    {
        if (v >= 1_000_000_000) return Trim(v / 1_000_000_000.0) + "b";
        if (v >= 1_000_000) return Trim(v / 1_000_000.0) + "m";
        if (v >= 1_000) return Trim(v / 1_000.0) + "k";
        return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A card price at <paramref name="scale"/> of itself (the price slider): "40k" at 50% is "20k", "9999" at 50% "5,000".
    /// Prices under 100k are written out in full so a small price keeps its digits; larger ones are shortened as usual.
    /// Anything that isn't a plain amount ("Offer", "1,000 per 10") is left as it is.
    /// </summary>
    public static string Scaled(string price, double scale)
    {
        if (Math.Abs(scale - 1) < 1e-9 || Parse(price) is not long v) return price;
        long s = Math.Max(1, (long)Math.Round(v * scale, MidpointRounding.AwayFromZero));
        return s < 100_000 ? s.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : Format(s);
    }

    static string Trim(double d) => d.ToString(d >= 100 ? "0" : d >= 10 ? "0.#" : "0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A typed price as a number: "5,000", "31.4k", "1.2m", "2b". Null when it is not a plain amount.</summary>
    public static long? Parse(string text) => MapleLite.Core.Market.MesoAmount.Parse(text);
}

/// <summary>
/// Links to item pages that open in the user's browser. kerning.gg's price feed refuses other programs, so nothing
/// is fetched from it.
/// </summary>
public static class ItemLinks
{
    public static string Kerning(CatalogItem it) => $"https://kerning.gg/item/{it.id}-{it.slug}";

    /// <summary>meso.watch goes by the item's number; maplestory-classic.com by its slug; Cozy Market and NiaMeowDB only have a search page.</summary>
    public static string For(string site, int itemId, CatalogItem? cat) => site switch
    {
        "meso" => itemId != 0 ? $"https://meso.watch/?item=basil%3A{itemId:D8}" : "https://meso.watch/",
        "classic" => cat is { slug.Length: > 0 } ? $"https://maplestory-classic.com/market/{cat.slug}" : "https://maplestory-classic.com/market",
        "cozy" => "https://cozy.markets/items",
        "nia" => "https://meowdb.com/msclassic/item-db",
        _ => cat != null ? Kerning(cat) : "https://kerning.gg/",
    };
}

/// <summary>Remembers the last price you typed for each item, so the next card starts pre-filled.</summary>
public sealed class PriceBook(string folder)
{
    readonly string _file = Path.Combine(folder, DataFiles.PriceBook);
    readonly Dictionary<string, string> _prices = Store.Load<Dictionary<string, string>>(Path.Combine(folder, DataFiles.PriceBook));

    public string? Get(int itemId, string? profile = null) => _prices.GetValueOrDefault(Key(itemId, profile));

    static string Key(int itemId, string? profile) => profile == null ? itemId.ToString() : $"{itemId}:{profile}";

    public void Set(int itemId, string price, string? profile = null)
    {
        price = price.Trim();
        string key = Key(itemId, profile);
        if (price.Length == 0 ? !_prices.Remove(key) : _prices.GetValueOrDefault(key) == price) return;
        if (price.Length > 0) _prices[key] = price;
        Store.Write(_file, JsonSerializer.Serialize(_prices));
    }
}

public sealed class HoverEntry
{
    public string Key { get; set; } = "";     // the card it was read on (tab, slot, item)
    public string Stats { get; set; } = "";
    public string Slots { get; set; } = "";
    public DateTime Utc { get; set; }
}

/// <summary>
/// Remembers the stats and slots last read (or typed) for each piece of equipment, so a card that is made again
/// later (after Clear list, a restart, or the item moving) starts with them instead of "not recorded".
/// </summary>
public sealed class HoverBook(string folder)
{
    readonly string _file = Path.Combine(folder, DataFiles.HoverStats);
    readonly Dictionary<int, List<HoverEntry>> _seen = Store.Load<Dictionary<int, List<HoverEntry>>>(Path.Combine(folder, DataFiles.HoverStats));

    public void Set(int id, string key, string stats, string slots)
    {
        if (id <= 0 || string.IsNullOrWhiteSpace(stats)) return;
        if (!_seen.TryGetValue(id, out var list)) _seen[id] = list = new();
        if (list.Any(e => e.Key == key && e.Stats == stats && e.Slots == slots)) return;
        list.RemoveAll(e => e.Key == key || (e.Stats == stats && e.Slots == slots));
        list.Add(new HoverEntry { Key = key, Stats = stats, Slots = slots, Utc = DateTime.UtcNow });
        if (list.Count > 12) list.RemoveAt(0);
        Store.Write(_file, JsonSerializer.Serialize(_seen));
    }

    /// <summary>
    /// The reading to give a new card of this item: the one made on the same slot, else the only one there is.
    /// Readings already showing on another card (inUse, as "stats|slots") belong to that copy and are left out.
    /// </summary>
    public HoverEntry? Find(int id, string key, ISet<string> inUse)
    {
        if (!_seen.TryGetValue(id, out var list)) return null;
        var free = list.Where(e => !inUse.Contains(e.Stats + "|" + e.Slots)).ToList();
        return free.LastOrDefault(e => e.Key == key) ?? (free.Count == 1 ? free[0] : null);
    }
}

/// <summary>
/// Items whose tooltip said they cannot be sold (quest item, one-of-a-kind, untradable). They get no card.
/// A known item is kept as "id:1234567", an unrecognised one by its slot picture as "pic:FINGERPRINT".
/// </summary>
public sealed class NotForSale(string folder)
{
    readonly string _file = Path.Combine(folder, DataFiles.NotForSale);
    readonly HashSet<string> _set = new(Store.Load<List<string>>(Path.Combine(folder, DataFiles.NotForSale)));

    public int Count => _set.Count;
    public bool Has(string ident) => ident.Length > 0 && _set.Contains(ident);
    public void Add(string ident)
    {
        if (_set.Add(ident)) Store.Write(_file, JsonSerializer.Serialize(_set.OrderBy(s => s, StringComparer.Ordinal).ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class FmObservation
{
    public int Id { get; set; }
    public long Unit { get; set; }       // price per single item
    public int Bundle { get; set; }
    public string Seller { get; set; } = "";
    public DateTime Utc { get; set; }
    public string Stats { get; set; } = "";
    public string Slots { get; set; } = "";
}

/// <summary>Asking prices you have seen in Free Market shops, kept on this computer only.</summary>
public sealed class FmPrices
{
    readonly List<FmObservation> _obs;
    readonly string _file;
    readonly object _lock = new();

    public FmPrices(string folder)
    {
        _file = Path.Combine(folder, DataFiles.FmPrices);
        _obs = Store.Load<List<FmObservation>>(_file);
    }

    public int Count { get { lock (_lock) return _obs.Count; } }

    /// <summary>Records a listing unless the same one (item, price, seller) was already seen in the last 6 hours.</summary>
    public bool Add(int id, long price, int bundle, string seller, string stats = "", string slots = "")
    {
        if (id <= 0 || price <= 0 || bundle <= 0) return false;
        long unit = Math.Max(1, price / Math.Max(1, bundle));
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(stats) && _obs.Any(o => o.Id == id && o.Unit == unit && o.Bundle == bundle && o.Seller == seller &&
                now - o.Utc < TimeSpan.FromHours(6))) return false;
            if (_obs.Any(o => o.Id == id && o.Unit == unit && o.Bundle == bundle && o.Seller == seller &&
                EquipmentReader.Profile(o.Stats, o.Slots) == EquipmentReader.Profile(stats, slots) && now - o.Utc < TimeSpan.FromHours(6)))
                return false;
            // Enrich the earlier uninspected row instead of counting its hover as another listing.
            if (!string.IsNullOrWhiteSpace(stats)) _obs.RemoveAll(o => o.Id == id && o.Unit == unit && o.Bundle == bundle && o.Seller == seller &&
                string.IsNullOrWhiteSpace(o.Stats) && now - o.Utc < TimeSpan.FromHours(6));
            _obs.Add(new FmObservation { Id = id, Unit = unit, Bundle = bundle, Seller = seller, Utc = now, Stats = stats, Slots = slots });
            return true;
        }
    }

    public void Save()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_obs);
        Store.Write(_file, json);
    }

    /// <summary>Median asking price over the last 7 days (or everything seen, if that week is thin).</summary>
    public (long price, int seen)? Rate(int id, string? stats = null, string? slots = null)
    {
        List<FmObservation> all;
        lock (_lock)
            all = _obs.Where(o => o.Id == id && (stats == null ||
                EquipmentReader.Profile(o.Stats, o.Slots) == EquipmentReader.Profile(stats, slots ?? ""))).ToList();
        if (all.Count == 0) return null;
        var recent = all.Where(o => DateTime.UtcNow - o.Utc < TimeSpan.FromDays(7)).ToList();
        var use = (recent.Count >= 3 ? recent : all).Select(o => o.Unit).OrderBy(v => v).ToList();
        long median = use.Count % 2 == 1 ? use[use.Count / 2] : (use[use.Count / 2 - 1] + use[use.Count / 2]) / 2;
        return (median, use.Count);
    }
}

/// <summary>The figures a price site shows for one item. Zero means "not read".</summary>
public sealed class SitePrice
{
    public long Last { get; set; }
    public long Avg3 { get; set; }
    public long Median10 { get; set; }
    public long Typical { get; set; }   // the list's "typical" / "usual price" column
    public DateTime Utc { get; set; }

    /// <summary>
    /// The chosen figure (0 usual/typical, 1 last sale, 2 last 3 avg, 3 last 10 median). A site that does not
    /// show the chosen one gives its usual price, or failing that whatever it does have.
    /// </summary>
    public long Pick(int figure)
    {
        long v = figure switch { 1 => Last, 2 => Avg3, 3 => Median10, _ => Typical };
        return v > 0 ? v : Typical > 0 ? Typical : Median10 > 0 ? Median10 : Last > 0 ? Last : Avg3;
    }
}

/// <summary>One item as read off a page. Names holds the text lines found beside the price, top to bottom.</summary>
public sealed record SiteRead(string[] Names, bool Truncated, long Last = 0, long Avg3 = 0, long Median10 = 0, long Typical = 0)
{
    /// <summary>The item's icon beside the name, when the list shows one: it tells apart items listed under one name (a scroll's grades).</summary>
    public MapleLite.Core.Imaging.Frame? Icon { get; init; }
}

/// <summary>Figures read off price sites while you had them open, kept on this computer only.</summary>
public sealed class SitePrices
{
    readonly Dictionary<string, Dictionary<int, SitePrice>> _prices;
    readonly string _file;
    readonly object _lock = new();

    public SitePrices(string folder)
    {
        _file = Path.Combine(folder, DataFiles.SitePrices);
        _prices = Store.Load<Dictionary<string, Dictionary<int, SitePrice>>>(_file);
    }

    public SitePrice? Get(string site, int id)
    {
        lock (_lock) return _prices.TryGetValue(site, out var s) && s.TryGetValue(id, out var p) ? p : null;
    }

    /// <summary>True when this changed what is stored. A figure that was not showing this time keeps its older value.</summary>
    public bool Set(string site, int id, SiteRead r)
    {
        lock (_lock)
        {
            if (!_prices.TryGetValue(site, out var s)) _prices[site] = s = new();
            s.TryGetValue(id, out var old);
            var now = new SitePrice
            {
                Last = r.Last > 0 ? r.Last : old?.Last ?? 0, Avg3 = r.Avg3 > 0 ? r.Avg3 : old?.Avg3 ?? 0,
                Median10 = r.Median10 > 0 ? r.Median10 : old?.Median10 ?? 0, Typical = r.Typical > 0 ? r.Typical : old?.Typical ?? 0, Utc = DateTime.UtcNow,
            };
            // A typical price twenty times off what the item actually sold for is a misread (kept "2" beside sales
            // of 8,999): the sale figures agree with each other, so they win and the typical one is dropped.
            long[] sales = new[] { now.Last, now.Avg3, now.Median10 }.Where(v => v > 0).Order().ToArray();
            if (now.Typical > 0 && sales.Length > 0)
            {
                long mid = sales[sales.Length / 2];
                if (now.Typical * 20 < mid || now.Typical > mid * 20) now.Typical = 0;
            }
            if (old != null && old.Last == now.Last && old.Avg3 == now.Avg3 && old.Median10 == now.Median10 && old.Typical == now.Typical) return false;
            s[id] = now;
            return true;
        }
    }

    public void Save()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_prices);
        Store.Write(_file, json);
    }
}

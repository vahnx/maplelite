using MapleLite.Core.Game;

namespace MapleLite.Plugins.Loot;

/// <summary>
/// Corrects item names OCR half-read from the small pickup notices ("Lugin•s Banana" → "Lupin's Banana") against the
/// game's item catalog, the shared one the host gives every plugin (context.Items). Without a catalog, names stay as read.
/// </summary>
internal sealed partial class ItemNames
{
    readonly List<(string Name, string Key)> _names;
    readonly Dictionary<string, string> _exact;
    readonly Dictionary<string, string> _keys;
    readonly ItemCatalog _catalog;

    public ItemNames(IEnumerable<string> names) : this(names, ItemCatalog.Empty) { }

    public ItemNames(ItemCatalog catalog) : this(catalog.Items.Select(i => i.name), catalog) { }

    ItemNames(IEnumerable<string> names, ItemCatalog catalog)
    {
        _catalog = catalog;
        _names = names.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => (n, Key(n))).Where(x => x.Item2.Length >= 2).ToList();
        _exact = _names.ToDictionary(x => x.Name, x => x.Name, StringComparer.OrdinalIgnoreCase);
        _keys = _names.GroupBy(x => x.Key).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Name);
    }

    public int Count => _names.Count;
    public bool IsKnown(string name) => _exact.ContainsKey(name);

    public LootDrop Fix(LootDrop drop, string? text = null) =>
        drop.Kind == "Item" && Match(drop.Name, truncated: IsTruncated(drop.Name, text)) is { } name && name != drop.Name
            ? drop with { Name = name } : drop;

    internal static bool IsTruncated(string name, string? text) => text != null &&
        (text.Contains(name + "...", StringComparison.OrdinalIgnoreCase) || text.Contains(name + "…", StringComparison.OrdinalIgnoreCase));

    /// <summary>The catalog picture of an item, or null when there isn't one.</summary>
    public string? IconPath(string name) => _catalog.IconPath(name);

    /// <summary>The catalog name closest to <paramref name="read"/>, or null when nothing is close enough to be sure.</summary>
    public string? Match(string read, bool allowPartial = true, bool truncated = false)
    {
        if (_exact.TryGetValue(read, out var exact)) return exact;
        if (read.Length > 100 || read.Contains('\n') || read.Contains('\r')) return null;
        string clean = NoticeSuffix().Replace(NoticeStamp().Replace(read.Trim(), ""), "").Trim();
        if (_exact.TryGetValue(clean, out exact)) return exact;
        string k = Key(clean);
        if (_keys.TryGetValue(k, out exact)) return exact;
        // A damaged count can be part of the saved name. Strip it only when the remaining name is known.
        var count = AttachedCount().Match(clean);
        if (count.Success && _keys.TryGetValue(Key(count.Groups["name"].Value), out exact)) return exact;
        if (k.Length < 6 || _names.Count == 0) return null;
        if (!allowPartial && !clean.Any(c => char.IsWhiteSpace(c) || c == '-')) return null;
        // An ellipsis in the original notice means the game shortened the label. Require a unique literal prefix.
        if (truncated)
        {
            var prefixes = _names.Where(item => item.Name.StartsWith(clean, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return prefixes.Length == 1 ? prefixes[0].Name : null;
        }
        string? best = null; int bestD = int.MaxValue, secondD = int.MaxValue;
        foreach (var (name, key) in _names)
        {
            if (Math.Abs(key.Length - k.Length) > Math.Max(3, k.Length / 3)) continue;
            int d = Distance(k, key);
            if (d < bestD) { secondD = bestD; bestD = d; best = name; }
            else if (d < secondD) secondD = d;
        }
        if (best != null)
        {
            // Ordinary edits cost two. Look-alike glyphs cost one, and the winner needs a clear lead.
            int allowed = allowPartial ? Math.Max(4, k.Length * 4 / 5) : Math.Clamp(k.Length / 2, 3, 8);
            if (bestD <= allowed && secondD - bestD >= (allowPartial ? 3 : 2)) return best;
        }
        return allowPartial ? MatchStart(k) : null;
    }

    /// <summary>
    /// When OCR lost the end of a name ("Jr. Necä" for "Jr. Necki Skin"), the one catalog name whose start matches what
    /// was read: at least 5 letters, at most a quarter of them wrong, and clearly better than any other name's start.
    /// </summary>
    string? MatchStart(string k)
    {
        if (k.Length < 6) return null;
        string? best = null; int bestD = int.MaxValue, secondD = int.MaxValue;
        foreach (var (name, key) in _names)
        {
            if (key.Length <= k.Length) continue;
            int d = Distance(k, key[..k.Length]);
            if (d < bestD) { secondD = bestD; bestD = d; best = name; }
            else if (d < secondD) secondD = d;
        }
        return best != null && bestD <= k.Length / 2 && secondD - bestD >= 4 ? best : null;
    }

    /// <summary>
    /// Whether a name read from a notice can be trusted as it stands. A name the catalog knows always can; otherwise it
    /// must read like an item name: plain letters (with spaces, apostrophes, periods, hyphens or colons), starting with
    /// a capital, at least 3 letters, no one-letter words and no capitals inside a word. That turns away the garble OCR
    /// makes of a fading notice ("C@tJpbh", "iC gon", "tr.:-ll", "C n"). A count left in the name ("Coupon x1" with
    /// another number after it) means the quantity can't be told, so that's turned away even when the name is known.
    /// </summary>
    public bool Trusted(string name, bool known)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => CountWord().IsMatch(w))) return false;
        if (known) return true;
        if (name.Count(char.IsAsciiLetter) < 3 || !char.IsAsciiLetterUpper(name[0])) return false;
        foreach (var w in words)
        {
            if (!w.All(c => char.IsAsciiLetter(c) || c is '\'' or '.' or '-' or ':')) return false;
            if (w.Count(char.IsAsciiLetter) < 2) return false;
            for (int i = 1; i < w.Length; i++)
                if (char.IsAsciiLetterUpper(w[i]) && char.IsAsciiLetterLower(w[i - 1])) return false;
        }
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[xz×][0-9il|!]{1,4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CountWord();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\[\d{1,2}[.:\s•]+\d{2}\s*[\]1|]\s*")]
    private static partial System.Text.RegularExpressions.Regex NoticeStamp();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[:;]\s*[ilI1|!]\s*$")]
    private static partial System.Text.RegularExpressions.Regex NoticeSuffix();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<name>.+?)\s+[x×]\s*\d[\dOoCcIiLl|!]{0,4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AttachedCount();

    // Letters and digits only, lowercased, with OCR's usual look-alikes folded together.
    static string Key(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).Select(c => c switch
    {
        '0' => 'o', '1' or '|' => 'l', '5' => 's', '8' => 'b', '3' => 'b', 'é' or 'è' or 'ê' or 'æ' => 'e', _ => c,
    }).ToArray());

    static int Distance(string a, string b)
    {
        Span<int> row = b.Length < 128 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        Span<int> previous = b.Length < 128 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) row[j] = j * 2;
        for (int i = 1; i <= a.Length; i++)
        {
            int diag = row[0]; row[0] = i * 2;
            int twoBack = diag;
            for (int j = 1; j <= b.Length; j++)
            {
                int old = row[j];
                row[j] = Math.Min(Math.Min(old + 2, row[j - 1] + 2), diag + GlyphCost(a[i - 1], b[j - 1]));
                // At notice size, OCR can read the joined strokes of “ti” as “j”.
                if (j >= 2 && a[i - 1] == 'j' && b[j - 2] == 't' && b[j - 1] == 'i')
                    row[j] = Math.Min(row[j], twoBack + 1);
                if (i >= 2 && a[i - 2] == 'r' && a[i - 1] == 'n' && b[j - 1] == 'm')
                    row[j] = Math.Min(row[j], previous[j - 1] + 1);
                previous[j - 1] = diag;
                twoBack = diag;
                diag = old;
            }
        }
        return row[b.Length];
    }

    static int GlyphCost(char read, char expected) => (read, expected) switch
    {
        _ when read == expected => 0,
        ('f', 'p') or ('s', 'b') or ('q', 'g') or ('h', 'm') or ('l', 'i') or ('i', 'l') => 1,
        _ => 2,
    };
}

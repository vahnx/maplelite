using MapleLite.Core.Game;

namespace MapleLite.Plugins.Loot;

/// <summary>
/// Corrects item names OCR half-read from the small pickup notices ("Lugin•s Banana" → "Lupin's Banana") against the
/// game's item catalog, the shared one the host gives every plugin (context.Items). Without a catalog, names stay as read.
/// </summary>
internal sealed partial class ItemNames
{
    readonly List<(string Name, string Key)> _names;
    readonly ItemCatalog _catalog;

    public ItemNames(IEnumerable<string> names) : this(names, ItemCatalog.Empty) { }

    public ItemNames(ItemCatalog catalog) : this(catalog.Items.Select(i => i.name), catalog) { }

    ItemNames(IEnumerable<string> names, ItemCatalog catalog)
    {
        _catalog = catalog;
        _names = names.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => (n, Key(n))).Where(x => x.Item2.Length >= 2).ToList();
    }

    public int Count => _names.Count;

    /// <summary>The catalog picture of an item, or null when there isn't one.</summary>
    public string? IconPath(string name) => _catalog.IconPath(name);

    /// <summary>The catalog name closest to <paramref name="read"/>, or null when nothing is close enough to be sure.</summary>
    public string? Match(string read)
    {
        string k = Key(read);
        if (k.Length < 3 || _names.Count == 0) return null;
        string? best = null; int bestD = int.MaxValue, secondD = int.MaxValue;
        foreach (var (name, key) in _names)
        {
            if (Math.Abs(key.Length - k.Length) > Math.Max(3, k.Length / 2)) continue;
            int d = Distance(k, key);
            if (d < bestD) { secondD = bestD; bestD = d; best = name; }
            else if (d < secondD) secondD = d;
        }
        if (best != null)
        {
            int len = Math.Max(k.Length, Key(best).Length);
            // Close (under 40% of the letters wrong) and clearly closer than the runner-up. Short names must match
            // exactly: one letter off is a different item ("Pass", a quest item the catalog lacks, isn't "Cass").
            double allowed = k.Length <= 5 ? 0 : len * 0.4;
            if (bestD <= allowed && (bestD == 0 || secondD - bestD >= 2)) return best;
        }
        return MatchStart(k);
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
        return best != null && bestD <= k.Length / 4 && secondD - bestD >= 2 ? best : null;
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

    /// <summary>The drop with its item name corrected, when the catalog knows it.</summary>
    public LootDrop Fix(LootDrop d) => d.Kind == "Item" && Match(d.Name) is { } name ? d with { Name = name } : d;

    // Letters and digits only, lowercased, with OCR's usual look-alikes folded together.
    static string Key(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).Select(c => c switch
    {
        '0' => 'o', '1' or '|' => 'l', '5' => 's', '8' => 'b', '3' => 'b', 'é' or 'è' or 'ê' or 'æ' => 'e', _ => c,
    }).ToArray());

    static int Distance(string a, string b)
    {
        var row = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) row[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int diag = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diag + (a[i - 1] == b[j - 1] ? 0 : 1));
                diag = old;
            }
        }
        return row[b.Length];
    }
}

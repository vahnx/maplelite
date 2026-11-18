using System.Text.RegularExpressions;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>
/// Reads an upgrade scroll's tooltip: its title is the scroll's full name and the description under it gives the
/// success rate and what it does ("Success rate: 60%, weapon attack +2"). The description can wrap over several
/// lines, so they are joined and the pieces with a percentage or a +/- number are kept, one per line.
/// </summary>
public static class ScrollReader
{
    static string Key(string s) => Regex.Replace(s.ToUpperInvariant(), @"[^A-Z0-9]", "");

    public static async Task<(string? stats, string why)> ReadWhyAsync(IOcr ocr, Frame frame, string name, (int x, int y)? cursor)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, "the card has no name");
        if (!ocr.Available) return (null, "no text reader is available");
        // the same whole-picture read the tooltip title check makes, so it's shared, not read twice
        var lines = await ocr.TryReadAsync(frame, new OcrRequest(frame.Bounds) { Scale = 2, Scaling = OcrScaling.Smooth });
        string want = Key(name);
        var titles = lines.Where(l => Key(l.Text) == want).ToList();
        if (titles.Count == 0) return (null, $"no tooltip titled \"{name}\" in the picture");
        string? best = null; double bestFar = double.MaxValue;
        foreach (var title in titles)
        {
            // the description: lines under the title, in the tooltip's width, until a gap
            var below = lines.Where(l => l.Cy() > title.Cy() + 4 && l.Cy() < title.Cy() + 240
                    && l.Left() > title.Left() - 40 && l.Left() < title.Left() + 260)
                .OrderBy(l => l.Cy()).ToList();
            var desc = new List<string>();
            double lastY = title.Cy();
            foreach (var l in below)
            {
                if (l.Cy() - lastY > 40) break;
                desc.Add(l.Text.Trim());
                lastY = l.Cy();
            }
            var stats = Stats(string.Join(" ", desc));
            if (stats.Length == 0) continue;
            double far = cursor is { } c ? Math.Abs(title.Left() - c.x) + Math.Abs(title.Cy() - c.y) : 0;
            if (far < bestFar) { bestFar = far; best = stats; }
        }
        return best != null ? (best, "") : (null, $"\"{name}\" shows, but no success rate or effect under it (no tooltip open, or it's covered)");
    }

    /// <summary>"Success rate: 60%, weapon attack +2. The success…" → "Success rate: 60%\nWeapon attack +2".</summary>
    public static string Stats(string description)
    {
        // split into sentences and comma pieces, but not at an abbreviation's dot ("weapon att. +2")
        var parts = Regex.Split(description, @"(?<=[.;])\s+(?=[A-Z])|,\s*")
            .Select(p => Regex.Replace(p.Trim().TrimEnd('.', ';'), @"\s+", " "))
            .Select(p => Regex.Replace(p, @"(\d)\s+%", "$1%"))
            .Select(p => Regex.Replace(p, @"([+-])\s+(\d)", "$1$2"))
            .Where(p => Regex.IsMatch(p, @"\d%|[+-]\d"))
            .Select(p => p.Length > 0 ? char.ToUpperInvariant(p[0]) + p[1..] : p)
            .Distinct()
            .ToList();
        return string.Join("\n", parts);
    }
}

/// <summary>Uses the title in a hovered item tooltip to correct a visually ambiguous icon.</summary>
public static class ItemTooltipReader
{
    static string Key(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    static async Task<List<OcrLine>> Near(IOcr ocr, Frame frame, (int x, int y)? cursor)
    {
        var lines = (await ocr.TryReadAsync(frame, new OcrRequest(frame.Bounds) { Scale = 2, Scaling = OcrScaling.Smooth })).ToList();
        if (cursor is { } c)
            lines = lines.Where(l => Math.Abs(l.Cy() - c.y) < 260 && l.Right() >= c.x - 360 && l.Left() <= c.x + 520).ToList();
        return lines;
    }

    public static async Task<CatalogItem?> ReadAsync(IOcr ocr, Frame frame, (int x, int y)? cursor, IReadOnlyCollection<CatalogItem> catalog)
    {
        var lines = await Near(ocr, frame, cursor);
        var hits = new List<(CatalogItem item, int score, OcrLine line)>();
        foreach (var line in lines)
            if (Match(line.Text, catalog) is { } hit) hits.Add((hit.item, hit.score, line));
        return Pick(hits, cursor);
    }

    static readonly (string word, string label)[] Flags =
    {
        ("QUESTITEM", "Quest item"), ("ONEOFAKIND", "One-of-a-kind item"), ("UNTRADABLE", "Untradable item"), ("UNTRADEABLE", "Untradable item"),
    };

    /// <summary>
    /// The hovered item's tooltip says it cannot be sold: "Quest Item", "One-of-a-kind Item" or "Untradable".
    /// Returns what it said, or null. With two equipment tooltips side by side (the hovered item and the worn one)
    /// there is no telling which the words belong to, so nothing is returned.
    /// </summary>
    public static async Task<string?> FlagAsync(IOcr ocr, Frame frame, (int x, int y)? cursor)
    {
        var keys = (await Near(ocr, frame, cursor)).Select(l => Key(l.Text)).ToList();
        if (keys.Count(k => k.Contains("REQLEV")) > 1) return null;
        var found = new List<string>();
        foreach (var k in keys.Where(k => k.Length <= 45))
            foreach (var (word, label) in Flags)
                if (Holds(k, word) && !found.Contains(label)) found.Add(label);
        return found.Count == 0 ? null : string.Join(", ", found);
    }

    /// <summary>The word is in the text, allowing one misread letter.</summary>
    static bool Holds(string text, string word)
    {
        for (int i = 0; i + word.Length <= text.Length; i++)
        {
            int miss = 0;
            for (int j = 0; j < word.Length && miss < 2; j++) if (text[i + j] != word[j]) miss++;
            if (miss < 2) return true;
        }
        return false;
    }

    /// <summary>
    /// Which of the item names found on screen is the hovered item's tooltip title. When the game shows the item
    /// you are wearing beside it for comparison, the two titles stand on one line and nothing is picked. Otherwise
    /// the best-read name wins, and of equally good ones the nearest to the pointer.
    /// </summary>
    static CatalogItem? Pick(List<(CatalogItem item, int score, OcrLine line)> hits, (int x, int y)? cursor)
    {
        if (hits.Count == 0) return null;
        double Far((CatalogItem item, int score, OcrLine line) h) => cursor is { } c ? Math.Abs((h.line.Left() + h.line.Right()) / 2 - c.x) + Math.Abs(h.line.Cy() - c.y) : 0;
        var top = hits.OrderByDescending(h => h.score / 1000).ThenBy(Far).First();
        var row = hits.Where(h => Math.Abs(h.line.Cy() - top.line.Cy()) < 14 && h.score / 1000 == top.score / 1000).OrderBy(h => h.line.Left()).ToList();
        // Two titles side by side: the hovered item and the one you are wearing. Which side is which changes with
        // where the tooltip has room, so neither is trusted.
        if (row.Select(h => h.item.id).Distinct().Count() > 1) return null;
        var rivals = hits.Where(h => h.score / 1000 == top.score / 1000 && h.item.id != top.item.id && Math.Abs(Far(h) - Far(top)) < 1).ToList();
        return rivals.Count > 0 ? null : top.item;
    }

    /// <summary>
    /// The one catalog item a line of text names, or null. An exact name always counts. A cut-off name counts only
    /// when a single item starts that way. A slightly misread name counts only when one item is clearly the closest:
    /// many scrolls differ by a word ("Earring Int…" / "Earring Luk…"), so a reading exactly as far from two of them
    /// is thrown away instead of guessed.
    /// </summary>
    public static (CatalogItem item, int score)? Match(string lineText, IReadOnlyCollection<CatalogItem> catalog)
    {
        // a tooltip title can carry marks after the name ("(M)", "(+2)"): match the name itself
        var text = Key(EquipmentReader.TitleParts(lineText).name);
        if (text.Length < 8) return null;
        CatalogItem? exact = null, prefix = null, near = null;
        int prefixes = 0, nearD = int.MaxValue, nextD = int.MaxValue;
        foreach (var item in catalog)
        {
            var name = Key(item.name);
            if (name.Length < 8) continue;
            if (text == name) { exact = item; break; }
            if (text.Length >= 12 && name.StartsWith(text, StringComparison.Ordinal) && name.Length - text.Length <= 10) { prefix = item; prefixes++; }
            if (Math.Abs(name.Length - text.Length) > 5) continue;
            int d = Distance(text, name);
            if (d < nearD) { nextD = nearD; nearD = d; near = item; }
            else if (d < nextD) nextD = d;
        }
        if (exact != null) return (exact, 10000 + text.Length);
        if (prefixes == 1) return (prefix!, 7000 + text.Length);
        if (prefixes > 1) return null;
        if (near == null) return null;
        int length = Key(near.name).Length;
        // close enough, the numbers in it read the same, and strictly closer than every other item (a tie is not guessed)
        if (nearD > Math.Max(2, length / 6) || nextD == nearD) return null;
        if (new string(text.Where(char.IsDigit).ToArray()) != new string(near.name.Where(char.IsDigit).ToArray())) return null;
        return (near, 5000 + length - nearD * 10);
    }

    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            var next = new int[b.Length + 1]; next[0] = i;
            for (int j = 1; j <= b.Length; j++) next[j] = Math.Min(Math.Min(next[j - 1] + 1, row[j] + 1), row[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            row = next;
        }
        return row[^1];
    }
}

using System.Text.RegularExpressions;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>An equipment tooltip as read: its stat lines (one per line, as the game spells them) and remaining upgrade slots.</summary>
public sealed record EquipmentDetails(string Stats, string Slots, IReadOnlyList<string> LinesRead);

/// <summary>
/// Reads equipment tooltips from the game picture: the stat lines between "Type:" and "Remaining Enhancements", which
/// the game shows when the mouse is on a piece of equipment (inventory, storage, shops). When you hover an item of a
/// kind you're wearing, the game shows your worn item's tooltip beside it for comparison; readers here never mix the two.
/// </summary>
public static class EquipmentReader
{
    /// <summary>True when <paramref name="next"/> has every stat named in <paramref name="previous"/> (a complete re-read, not a partial one).</summary>
    public static bool CoversRecordedStats(string previous, string next)
    {
        static HashSet<string> Fields(string value) => value.Split('\n').Where(l => l.Contains(':'))
            .Select(l => Key(FixLabel(l).Split(':')[0])).ToHashSet();
        return Fields(previous).IsSubsetOf(Fields(next));
    }
    static string Clean(string s) => s.Trim().TrimStart('•', '·', '+', '*', '.', ',', ' ').Trim();
    static string Key(string s) => Regex.Replace(s.ToUpperInvariant(), @"[^A-Z0-9]", "");

    static readonly Regex TitleExtra = new(@"\s*\(\s*(?:\+\s*(?<up>\d+)|[MF])\s*\)\s*$", RegexOptions.IgnoreCase);

    /// <summary>
    /// A tooltip title split into the item's name and its upgrade count. The game adds marks after the name: "(M)" or
    /// "(F)" for male and female versions, and "(+3)" for upgrades applied ("Blue Pao Bottoms (M)", "Meba (+2)").
    /// </summary>
    public static (string name, int upgrades) TitleParts(string title)
    {
        string name = Clean(title);
        int upgrades = 0;
        for (Match m; (m = TitleExtra.Match(name)).Success;)
        {
            if (m.Groups["up"].Success) upgrades = int.Parse(m.Groups["up"].Value);
            name = name[..m.Index];
        }
        return (name.Trim(), upgrades);
    }

    /// <summary>
    /// True for the tooltip's last line, "Remaining Enhancements: N", as the reader may give it: a letter dropped or
    /// split off ("Enhanceme ts", "Enhancemen s"), so it goes by "Remaining" and the start of "Enhancements".
    /// </summary>
    public static bool IsLastLine(string line)
    {
        var k = Key(line);
        return k.StartsWith("REMAINING", StringComparison.Ordinal) && k.Contains("ENHANCEM", StringComparison.Ordinal)
               || k.Contains("ENHANCEMENTS", StringComparison.Ordinal);
    }

    /// <summary>True when a line of text is a tooltip title for the named item, marks and all.</summary>
    public static bool IsTitleOf(string line, string name) => Key(TitleParts(line).name) == Key(name) && Key(name).Length > 0;

    /// <summary>A comparable fingerprint of a reading: its stat lines in any order, without spaces, and the slots.</summary>
    public static string Profile(string stats, string slots) =>
        string.Join("|", stats.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => Regex.Replace(s.ToUpperInvariant(), @"\s+", "")).OrderBy(s => s, StringComparer.Ordinal)) + "|slots=" + slots.Trim();

    public static async Task<EquipmentDetails?> ReadAsync(IOcr ocr, Frame frame, string name) => (await ReadWhyAsync(ocr, frame, name)).details;

    /// <summary>
    /// The tooltip of the named item, and when nothing comes of it, why (for status lines and diagnostics).
    /// When two tooltips of this item are showing (the hovered copy and the identical item you wear),
    /// <paramref name="choose"/> may pick the hovered one, for example by setting aside the stats known to be worn
    /// (see <see cref="AllAsync"/>). Without a pick, nothing is returned: stats are never guessed.
    /// </summary>
    public static async Task<(EquipmentDetails? details, string why)> ReadWhyAsync(IOcr ocr, Frame frame, string name,
        Func<IReadOnlyList<EquipmentDetails>, EquipmentDetails?>? choose = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, "no item name");
        if (!ocr.Available) return (null, "no text reader is available");
        var overview = await Overview(ocr, frame);
        var candidates = OcrBlocks.WithJoinedNeighbours(overview).Where(l => IsTitleOf(l.Text, name)).ToList();
        if (candidates.Count == 0) return (null, $"no tooltip titled \"{name}\" in the picture");
        var results = new List<EquipmentDetails>();
        int noEnd = 0, unparsed = 0;
        foreach (var title in candidates)
        {
            var end = EndOf(overview, title);
            if (end == null) { noEnd++; continue; }
            var details = await ReadBox(ocr, frame, overview, title, end);
            if (details != null && !results.Any(d => Profile(d.Stats, d.Slots) == Profile(details.Stats, details.Slots) && d.LinesRead.SequenceEqual(details.LinesRead))) results.Add(details);
            else if (details == null) unparsed++;
        }
        if (results.Count == 1) return (results[0], "");
        if (results.Count > 1)
        {
            if (choose?.Invoke(results) is { } picked) return (picked, "");
            return (null, $"{results.Count} tooltips of {name} are showing (the worn one beside it): can't tell which is hovered yet (hover any other item of the same kind once, so the worn one's stats are known)");
        }
        if (unparsed > 0) return (null, "the tooltip's stat lines couldn't be read");
        return (null, noEnd > 0 ? $"\"{name}\" shows, but no tooltip with a \"Remaining Enhancements\" line under it (no tooltip open, or it's covered or cut off)" : $"no tooltip titled \"{name}\"");
    }

    /// <summary>
    /// Every equipment tooltip showing, with its title as read, when there are two or more: the hovered one and,
    /// beside it, the worn one the game shows for comparison. A tooltip whose title isn't the hovered item's is what is
    /// worn, which tells the two apart when you later hover the identical item. Empty when only one tooltip shows.
    /// </summary>
    public static async Task<List<(string title, EquipmentDetails details)>> AllAsync(IOcr ocr, Frame frame)
    {
        var found = new List<(string, EquipmentDetails)>();
        if (!ocr.Available) return found;
        var overview = await Overview(ocr, frame);
        var ends = overview.Where(l => IsLastLine(l.Text)).ToList();
        if (ends.Count < 2) return found;   // one tooltip: nothing to compare
        var joined = OcrBlocks.WithJoinedNeighbours(overview);
        foreach (var end in ends)
        {
            // the title is the topmost line lined up with the last one, within a tooltip's height above it (about 175 px;
            // further up is the window behind, such as the inventory's "Equip" tab); of words joined on that row, all of them
            var lined = joined.Where(l => l.Cy() < end.Cy() - 20 && l.Cy() > end.Cy() - 215 && Math.Abs(l.Left() - end.Left()) < 25).ToList();
            if (lined.Count == 0) continue;
            double topY = lined.Min(l => l.Cy());
            var title = lined.Where(l => l.Cy() < topY + 5).OrderByDescending(l => l.Text.Length).First();
            if (await ReadBox(ocr, frame, overview, title, end) is { } details)
                found.Add((TitleParts(title.Text).name, details));
        }
        return found;
    }

    /// <summary>The whole picture read once at 1x; shared (cached) with every reader that asks the same of this frame.</summary>
    static Task<IReadOnlyList<OcrLine>> Overview(IOcr ocr, Frame frame) =>
        ocr.TryReadAsync(frame, new OcrRequest(frame.Bounds) { Scale = 1, Scaling = OcrScaling.Smooth });

    static OcrLine? EndOf(IReadOnlyList<OcrLine> overview, OcrLine title) =>
        overview.Where(l => l.Cy() > title.Cy() && l.Cy() < title.Cy() + 500 &&
            Math.Abs(l.Left() - title.Left()) < 25 && IsLastLine(l.Text))
            .OrderBy(l => l.Cy()).FirstOrDefault();

    /// <summary>Reads one tooltip box, from its title line down to its "Remaining Enhancements" line.</summary>
    static async Task<EquipmentDetails?> ReadBox(IOcr ocr, Frame frame, IReadOnlyList<OcrLine> overview, OcrLine title, OcrLine end)
    {
        int x = Math.Max(0, (int)title.Left() - 4), y = Math.Max(0, (int)title.Cy() - 9);
        int width = Math.Min(226, frame.W - x), height = Math.Min((int)end.Cy() + 13 - y, frame.H - y);
        // Stop at a tooltip beside this one (the worn item's, shown for comparison): its edge and text are not ours.
        // Another tooltip is known by its own "Type:" or "Remaining Enhancements" line starting further right.
        foreach (var l in overview)
            if (l.Left() > x + 60 && l.Left() < x + width + 20 && l.Cy() > y && l.Cy() < y + height + 40 &&
                (IsLastLine(l.Text) || Clean(l.Text).StartsWith("Type", StringComparison.OrdinalIgnoreCase)))
                width = Math.Min(width, (int)l.Left() - x - 14);
        if (width < 8 || height < 8) return null;
        // The tooltip is translucent: the world shows through, darker. White and coloured stat text (bonus and
        // penalty signs included) stays bright, so bright pixels are the text.
        var rows = await ocr.ReadTextRowsAsync(frame, new Region(x, y, width, height), p => Pixel.Max(p) >= 160);
        var read = rows.Select(r => r.Text).ToList();
        var details = Parse(read);
        if (details == null) return null;
        int upgrades = TitleParts(title.Text).upgrades;
        if (upgrades > 0) details = details with { Stats = details.Stats + "\nEnhancements applied: +" + upgrades };
        return details with { LinesRead = read };
    }

    static readonly string[] Labels =
    {
        "STR", "DEX", "INT", "LUK", "HP", "MP", "Max HP", "Max MP", "Weapon Attack", "Magic Attack", "Weapon Def", "Magic Def",
        "Weapon Defense", "Magic Defense", "Accuracy", "Avoidability", "Speed", "Jump", "Attack Speed", "Hands", "Knockback",
    };

    static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>Puts a stat line's label back to the game's spelling and capitals ("weapon Dei: 12" becomes "Weapon Def: 12").</summary>
    public static string FixLabel(string line)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0) return line;
        // specks read as dots or bullets around the text are not part of it
        string label = line[..colon].Trim(' ', '.', ',', '•', '·', '*'), rest = line[(colon + 1)..].Trim(' ', '•', '·', '*');
        if (label.Length == 0) return line;
        var ranked = Labels.Select(l => (l, d: Distance(l.ToUpperInvariant(), label.ToUpperInvariant()))).OrderBy(x => x.d).ToList();
        var best = ranked[0];
        // a three-letter stat with one letter misread ("DEY") is still that stat when no other label is as close
        bool lone = label.Length == 3 && best.d == 1 && best.l.Length == 3 && ranked[1].d >= 2;
        if (lone || best.d <= (label.Length <= 3 ? 0 : label.Length <= 6 ? 1 : 2)) label = best.l;
        else label = string.Join(" ", label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        // the speed is one of a few words; a misread one ("Slovy") goes back to the nearest
        if (label == "Attack Speed")
        {
            // the word is the letters up to the first digit or bracket; "w" is often read as "vy"
            int cut = rest.IndexOfAny("0123456789(".ToCharArray());
            string tail = cut < 0 ? "" : rest[cut..].Trim();
            string word = new string((cut < 0 ? rest : rest[..cut]).Where(char.IsLetter).ToArray()).ToUpperInvariant().Replace("VY", "W");
            var near = new[] { "Fastest", "Faster", "Fast", "Normal", "Slow", "Slower", "Slowest" }
                .Select(s => (s, d: Distance(s.ToUpperInvariant(), word))).OrderBy(x => x.d).ToList();
            if (word.Length >= 3 && near[0].d <= 2 && near[1].d > near[0].d) rest = near[0].s + (tail.Length > 0 ? " " + tail : "");
        }
        return label + ": " + rest;
    }

    public static string FixLabels(string stats) => string.Join("\n", stats.Split('\n').Select(FixLabel));

    /// <summary>The stat lines and remaining slots from a tooltip's text lines, or null when there is no complete stat block.</summary>
    public static EquipmentDetails? Parse(IEnumerable<string> text)
    {
        var stats = new List<string>(); bool body = false;
        foreach (var raw in text)
        {
            var line = Clean(raw);
            if (line.StartsWith("Type:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Type :", StringComparison.OrdinalIgnoreCase)) { body = true; continue; }
            if (!body) continue; // exclude level/class requirements and comparison headers
            if (IsLastLine(line))
            {
                // "Remaining Enhancements: 7", also when the reader breaks the word ("Enhanceme ts: 7") or chat
                // running past the tooltip adds text after the number
                var slots = Regex.Match(line, @"^Remain\S*\s+Enh\S*(?:\s+\S{1,4})?\s*:?\s*(\d{1,2})(?!\d)", RegexOptions.IgnoreCase);
                return slots.Success && stats.Count > 0 ? new(string.Join("\n", stats), slots.Groups[1].Value, Array.Empty<string>()) : null;
            }
            // the colon is sometimes not read ("DEY +2"): a short label followed by a signed number is still a stat line
            if (!line.Contains(':') && Regex.Match(line, "^([A-Za-z][A-Za-z .]{1,16}?)[ .;]*([+-][0-9].*)$") is { Success: true } bare)
                line = bare.Groups[1].Value.Trim() + ": " + bare.Groups[2].Value;
            // the base and bonus in brackets are read with stray gaps: "(1 +1)" is "(1+1)"
            line = Regex.Replace(line, "[(][^()]*[)]", m => m.Value.Replace(" ", ""));
            line = Regex.Replace(line, "(?<=[0-9])[•·](?=[0-9])", "");   // a speck read as a dot inside a number
            if (line.Contains(':')) stats.Add(FixLabel(line)); // preserve signs and parenthesized base +/- bonus text
        }
        return null;
    }
}

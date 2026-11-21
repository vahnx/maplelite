using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MapleLite.Core.Game;

namespace MapleLite.Plugins.DpsSimulator;

internal static class ClassicWiki
{
    const string Root = "https://mapleclassic.wiki";
    public const string MonstersUrl = Root + "/wiki/Monsters";
    public const string FormulaUrl = "https://meowdb.com/msclassic/guides/explaining-the-damage-formula";
    static readonly HttpClient Web = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly Regex Rows = new(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex Cells = new(@"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex Link = new("href=\"(/wiki/[^\"#]+)\"", RegexOptions.IgnoreCase);
    static readonly Regex Tags = new("<[^>]+>");
    static readonly Dictionary<string, string> StatLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["W.ATK"] = "Weapon Attack", ["M.ATK"] = "Magic Attack", ["W.DEF"] = "Weapon Def", ["M.DEF"] = "Magic Def",
        ["ACC"] = "Accuracy", ["EVA"] = "Avoidability", ["HP"] = "Max HP", ["MP"] = "Max MP",
        ["Crit Damage"] = "Crit Damage", ["Crit Rate"] = "Crit Rate",
    };
    static readonly Regex StatCells = new(
        """<span class="infobox-stat-label">([^<]+)</span>\s*<span class="infobox-stat-value[^"]*">([^<]+)</span>""");

    static ClassicWiki() => Web.DefaultRequestHeaders.UserAgent.ParseAdd("MapleLite-DpsSimulator/1.0");

    public static string GearUrl(CatalogItem item) => Root + "/wiki/" + Uri.EscapeDataString(item.name.Replace(' ', '_'));

    public static async Task<Monster[]> RefreshMonstersAsync(CancellationToken token)
    {
        var monsters = ReadMonsters(await Web.GetStringAsync(MonstersUrl, token));
        if (monsters.Length < 30) throw new FormatException("The wiki returned an incomplete monster table. Your saved list is still available.");
        return monsters;
    }

    internal static Monster[] ReadMonsters(string html)
    {
        var monsters = new Dictionary<string, Monster>();
        foreach (Match row in Rows.Matches(html))
        {
            var cells = Cells.Matches(row.Groups[1].Value);
            if (cells.Count < 12) continue;
            var link = Link.Match(cells[1].Groups[1].Value);
            if (!link.Success || !int.TryParse(Cell(2), NumberStyles.None, CultureInfo.InvariantCulture, out int level)
                || !long.TryParse(Cell(3).Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out long hp)
                || !int.TryParse(Cell(7), out int physical) || !int.TryParse(Cell(8), out int magic) || !int.TryParse(Cell(10), out int avoid))
                continue;
            if (level < 0 || hp <= 0 || physical < 0 || magic < 0 || avoid < 0) continue;
            string name = Cell(1);
            string path = WebUtility.HtmlDecode(link.Groups[1].Value);
            if (name.Length == 0) continue;
            monsters.TryAdd(path, new(path, name, level, hp, physical, magic, avoid, Cell(11), Root + path));
            string Cell(int index) => Plain(cells[index].Groups[1].Value);
        }
        return monsters.Values.OrderBy(m => m.Level).ThenBy(m => m.Name).ToArray();
    }

    static string Plain(string html) => WebUtility.HtmlDecode(Tags.Replace(html, "")).Trim();

    public static async Task<string> GearStatsAsync(CatalogItem item, CancellationToken token)
    {
        var html = await Web.GetStringAsync(GearUrl(item), token);
        string title = Plain(Regex.Match(html, """<meta property="og:title" content="([^"]+)">""").Groups[1].Value);
        if (!title.Equals(item.name, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("The wiki page did not match the selected item. Enter the stats from its tooltip.");

        var lines = new List<string>();
        foreach (Match pair in StatCells.Matches(html))
        {
            string label = Plain(pair.Groups[1].Value), value = Plain(pair.Groups[2].Value);
            if (!StatLabels.TryGetValue(label, out var normalized)) normalized = label;
            if (GearStats.Names.Contains(normalized, StringComparer.OrdinalIgnoreCase) && value.Any(char.IsDigit))
                lines.Add($"{normalized}: {value}");
        }
        if (lines.Count == 0) throw new FormatException("The wiki page has no usable gear stats. Enter the stats from its tooltip.");
        return string.Join("\n", lines);
    }

    public static readonly Monster[] StarterMonsters =
    [
        Starter("Snail", 1, 45, 0, 0, 0), Starter("Slime", 6, 115, 10, 5, 0, "Lightning"),
        Starter("Orange Mushroom", 8, 142, 0, 0, 0), Starter("Wild Boar", 25, 510, 20, 10, 19),
        Starter("King Slime", 32, 16820, 15, 15, 11), Starter("Jr. Wraith", 34, 982, 30, 30, 25, "Fire, Holy"),
    ];

    static Monster Starter(string name, int level, long hp, int wdef, int mdef, int avoid, string weakness = "")
    {
        string path = "/wiki/" + name.Replace(' ', '_');
        return new(path, name, level, hp, wdef, mdef, avoid, weakness, Root + path);
    }
}

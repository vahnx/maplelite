using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace MapleLite.Plugins.SaleCard;

/// <summary>Versioned item-list interchange, with RFC-style quoted fields and spreadsheet-safe text.</summary>
public static class CardCsv
{
    static readonly string[] Header = ["Key", "ItemId", "Name", "Quantity", "Price", "Stats", "Slots", "Buying", "Included", "PricePercent", "Stacks"];
    static string Safe(string s) => s.Length > 0 && "'=+-@\t\r\n".Contains(s[0]) ? "'" + s : s;
    static string Unescape(string s) => s.Length > 1 && s[0] == '\'' && "'=+-@\t\r\n".Contains(s[1]) ? s[1..] : s;
    static string Q(string s) => "\"" + Safe(s).Replace("\"", "\"\"") + "\"";

    public static string Export(IEnumerable<SaleItem> items)
    {
        var text = new StringBuilder(string.Join(",", Header) + "\r\n");
        foreach (var i in items)
            text.AppendLine(string.Join(",", new[] { i.Key, i.ItemId.ToString(CultureInfo.InvariantCulture), i.Name,
                i.Qty, i.FinalPrice, i.Stats, i.Slots, i.Buying.ToString(), i.Include.ToString(),
                i.PricePercent.ToString(CultureInfo.InvariantCulture), i.Stacks.ToString(CultureInfo.InvariantCulture) }.Select(Q)));
        return text.ToString();
    }

    public static IReadOnlyList<SavedItem> Parse(string csv)
    {
        if (csv.Length > 5_000_000) throw new FormatException("CSV exceeds the 5 MB text limit.");
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF'))) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        try
        {
            var header = parser.ReadFields();
            if (header == null || !header.SequenceEqual(Header)) throw new FormatException("Use the columns from a MapleLite CSV export, in the same order.");
            var rows = new List<SavedItem>();
            var keys = new HashSet<string>();
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields();
                if (fields == null) continue;
                if (fields.Length != Header.Length) throw new FormatException($"Row {rows.Count + 2}: expected {Header.Length} columns.");
                var f = fields.Select(Unescape).ToArray();
                if (rows.Count >= 10_000 || f.Any(s => s.Length > 20_000)) throw new FormatException("CSV contains too many rows or an oversized field.");
                if (string.IsNullOrWhiteSpace(f[0]) || !keys.Add(f[0]) || string.IsNullOrWhiteSpace(f[2]) ||
                    !int.TryParse(f[1], out int id) || id < 0 || !bool.TryParse(f[7], out bool buying) ||
                    !bool.TryParse(f[8], out bool include) || !int.TryParse(f[9], out int percent) || percent is < 1 or > 200 ||
                    !int.TryParse(f[10], out int stacks) || stacks < 1)
                    throw new FormatException($"Row {rows.Count + 2}: invalid ID, name, boolean, percentage, stack count, or duplicate key.");
                rows.Add(new SavedItem { Key = f[0], ItemId = id, Name = f[2], Qty = f[3], Custom = f[4], Stats = f[5], Slots = f[6],
                    Buying = buying, Include = include, PricePercent = percent, Stacks = stacks });
            }
            return rows;
        }
        catch (MalformedLineException e) { throw new FormatException("Malformed CSV quoting near line " + e.LineNumber, e); }
    }
}

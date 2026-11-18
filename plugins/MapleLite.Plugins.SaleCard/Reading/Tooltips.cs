using MapleLite.Core.Game;
using System.Text.RegularExpressions;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Plugins.SaleCard;

public sealed record ShopRow(int ItemId, string Name, int Bundle, long Price, double Score, string RawText, int Top = 0);

/// <summary>A sold listing (greyed out in your own shop): the items its name can stand for, its bundle and price.</summary>
public sealed record SoldRow(IReadOnlyList<int> ItemIds, string Name, int Bundle, long Price, int Top);

public sealed class ShopRead
{
    public string Seller = "";
    public bool IsOwnerView;
    public int RowsSeen;
    public List<ShopRow> Rows = new();
    public List<SoldRow> Sold = new();
    public int SoldUnread;
    public string Debug = "";
    public bool Unchanged; // same picture as the previous read: nothing new to record
    public int Left, Right;
}

/// <summary>
/// Reads the item list of a player shop that is open on screen: each row's icon (matched to an item)
/// and its "N for P mesos" line. Purely from the captured picture. One instance remembers the last shop it read.
/// </summary>
public sealed class ShopReader
{
    static int R(int p) => (p >> 16) & 255;
    static int G(int p) => (p >> 8) & 255;
    static int B(int p) => p & 255;
    static bool Is(int p, int r, int g, int b, int tol = 4) =>
        Math.Abs(R(p) - r) <= tol && Math.Abs(G(p) - g) <= tol && Math.Abs(B(p) - b) <= tol;
    static bool Blue(int p) => Is(p, 205, 217, 227);     // text panel of a row
    static bool Beige(int p) => Is(p, 225, 223, 213);    // icon cell of a row

    static readonly Regex PriceLine = new(@"^\s*(\d+)\s*for\s*(\d{1,3}(?:,\d{3})*)\s*mesos\s*$", RegexOptions.IgnoreCase);
    const int PanelW = 162;
    long _lastHash;
    ShopRead? _last;

    public async Task<ShopRead?> ReadAsync(IOcr ocr, Vision vision, Frame f)
    {
        // 1. The left edge of the rows' text panels: a column where blue panels start just right of beige icon cells.
        // Other windows use the same colours (the owner's "sold items" list has taller rows), so every such column is
        // tried, most blue first, and the first whose panels have the height of a listing row is the shop.
        var columns = new List<(int x, int n)>();
        for (int x = 45; x < f.W - PanelW - 2; x++)
        {
            int n = 0;
            for (int y = 60; y < f.H - 40; y++)
                if (Blue(f.At(x, y)) && Blue(f.At(x + 2, y)) && !Blue(f.At(x - 1, y)) && Beige(f.At(x - 4, y))) n++;
            if (n >= 20) columns.Add((x, n));
        }
        int bestX = -1;
        var rows = new List<(int top, int h)>();
        var sold = new List<(int top, int h)>();
        foreach (var (x, _) in columns.OrderByDescending(c => c.n))
        {
            var (found, faded) = Rows(f, x);
            if (found.Count == 0) continue;
            bestX = x; rows = found; sold = faded;
            break;
        }
        if (bestX < 0) return null;

        // 3. Icon cell bounds (beige square left of the panel).
        int cellRight = bestX - 1;
        while (cellRight > bestX - 8 && !Beige(f.At(cellRight, rows[0].top + 1))) cellRight--;
        // The icon may touch the sampled row. Walking through beige stops inside
        // that icon, falsely reporting a narrow cell and rejecting the whole shop.
        int cellLeft = bestX - 36;
        int cellW = cellRight - cellLeft + 1;
        var read = new ShopRead { Left = cellLeft, Right = bestX + PanelW, RowsSeen = rows.Count, Debug = $"panelX {bestX} rows {rows.Count} first {rows[0].top}+{rows[0].h} cell {cellLeft}..{cellRight} ({cellW})" };
        if (cellW < 30 || cellLeft < 0) return read;

        // 4. Text (skipped when the list looks exactly like last time).
        long hash = 17;
        foreach (var (top, h) in rows)
            for (int y = top; y < top + h; y++)
                for (int x = cellLeft; x < bestX + PanelW; x++)
                    hash = hash * 31 + f.At(x, y);
        // Include the seller and owner controls: another shop with identical
        // listings must not inherit the previous shop's identity or ownership.
        for (int y = Math.Max(0, rows[0].top - 105); y < rows[0].top; y++)
            for (int x = cellLeft; x < bestX + PanelW; x++) hash = hash * 31 + f.At(x, y);
        if (hash == _lastHash && _last != null) { _last.Unchanged = true; return _last; }

        // Seller name plate sits above the first row, under the seller's character.
        var plate = await ocr.TryReadAsync(f, cellLeft + 4, rows[0].top - 24, 92, 18, 4);
        read.Seller = plate.Count > 0 ? new string(plate[0].Text.Where(ch => !char.IsWhiteSpace(ch)).ToArray()) : "";

        var controls = await ocr.TryReadAsync(f, cellLeft + 125, rows[0].top - 105, 92, 80, 2);
        read.IsOwnerView = controls.Any(l => Regex.Replace(l.Text.ToUpperInvariant(), @"[^A-Z]", "").Contains("CLOSESTORE"));

        int bg = unchecked((int)0xFFE1DFD5);
        // Icons: the matcher already slides each icon a few pixels around, so one centred crop per row is enough.
        var icons = await Task.Run(() => rows.AsParallel().AsOrdered()
            .Select(r => vision.MatchAt(f, cellLeft + 1, r.top + Math.Max(0, (r.h - Vision.Slot) / 2), bg)).ToArray());
        int rowIndex = -1;
        foreach (var (top, h) in rows)
        {
            rowIndex++;
            // Read each row separately at 2x: larger scales often turn 9 into g
            // or omit an entire group of price digits in this pixel font.
            var mine = await ocr.TryReadAsync(f, bestX, top, PanelW, h, 2, 4);
            string raw = string.Join(" | ", mine.Select(l => l.Text));
            int glyphs = PriceGlyphs(f, bestX, top, h, 100);
            var priceText = PriceIn(mine, glyphs);
            if (priceText == null)
            {
                // The pixel font loses digits at one enlargement and not another (a 9 read as g, a group of digits
                // dropped): read just the price line again, other ways, and keep the read whose characters match
                // what is drawn. A price that never matches is skipped, never saved short.
                // (on a shop capture, "1,799,999" came out right only at 2x smooth on the price line alone)
                var crops = new[] { new Region(bestX, top + h / 2 - 2, PanelW, h - h / 2 + 4), new Region(bestX, top + h / 2 + 2, PanelW, h - h / 2 - 4) };
                var (again, lines) = await ocr.ReadUntilAsync(f, OcrBlocks.Variants(crops), l => PriceIn(l, glyphs));
                if (again != null) { priceText = again; raw += " | retry: " + string.Join(" ", lines.Select(l => l.Text)); }
            }
            if (priceText == null) { read.Debug += $"\n  skip y{top}: no price line that matches the {glyphs} characters drawn [{raw}]"; continue; }
            if (!int.TryParse(Digits(priceText.Groups[1].Value), out int bundle) || bundle < 1) continue;
            if (!long.TryParse(Digits(priceText.Groups[2].Value), out long price) || price < 1) continue;

            var (id, score) = icons[rowIndex];
            string name = mine.Count > 0 && !PriceLine.IsMatch(Normalise(mine[0].Text)) ? mine[0].Text : "";
            // Many scrolls share an identical icon. The visible shop name must agree;
            // a truncated name with several candidates cannot safely identify a price.
            var candidates = Candidates(vision, name);
            var named = candidates.FirstOrDefault(c => c.id == id);
            if (named == null)
            {
                // Items that share one picture (scrolls): the name decides, if exactly one named item's own icon fits the cell.
                var fits = candidates.Take(12)
                    .Select(c => (c, own: vision.MatchAt(f, cellLeft + 1, top + Math.Max(0, (h - Vision.Slot) / 2), bg, c.id).score))
                    .Where(x => x.own >= 0.9 && x.own >= score - 0.03).ToList();
                if (fits.Count == 1) { named = fits[0].c; score = fits[0].own; }
            }
            if (named == null) { read.Debug += $"\n  skip y{top}: {candidates.Count} names match, icon {id} {score:F2} [{raw}]"; continue; }
            read.Rows.Add(new ShopRow(named.id, name, bundle, price, score, raw, top));
        }
        // Sold listings (greyed out in the owner's view): read just enough to know which listing it was.
        foreach (var (top, h) in sold)
        {
            // grey lettering on a pale panel: keep only the lettering (MapleLite's dark-text cleanup), then read as usual
            var mine = await ocr.TryReadAsync(f, new OcrRequest(new Region(bestX, top, PanelW, h)) { Scale = 2, Scaling = OcrScaling.Smooth, Pad = 4, Preprocess = OcrPreprocessor.DarkText(200) });
            var priceText = PriceIn(mine, PriceGlyphs(f, bestX, top, h, 200));
            string name = mine.Count > 0 && !PriceLine.IsMatch(Normalise(mine[0].Text)) ? mine[0].Text : "";
            if (priceText == null || !int.TryParse(Digits(priceText.Groups[1].Value), out int bundle) || !long.TryParse(Digits(priceText.Groups[2].Value), out long price))
            {
                read.Debug += $"\n  sold y{top}: unreadable [{string.Join(" | ", mine.Select(l => l.Text))}]";
                read.SoldUnread++;
                continue;
            }
            read.Sold.Add(new SoldRow(Candidates(vision, name).Select(c => c.id).ToList(), name, bundle, price, top));
        }
        read.Debug += $"\n  sold rows {sold.Count}";
        _lastHash = hash;
        _last = read;
        return read;
    }

    /// <summary>
    /// The "N for P mesos" line among what was read, if its characters agree with the number of characters drawn
    /// (counted from the picture): OCR that dropped or invented a character never gives a price.
    /// </summary>
    static Match? PriceIn(IEnumerable<OcrLine> lines, int glyphs)
    {
        foreach (var l in lines)
        {
            var m = PriceLine.Match(Regex.Replace(Normalise(l.Text), @"(?<=[\d,])\s+(?=[\d,])", ""));
            if (m.Success && glyphs == m.Groups[1].Value.Length + m.Groups[2].Value.Length + 8) return m;   // + "for" + "mesos"
        }
        return null;
    }

    static bool Faded(int p) => Is(p, 242, 245, 248, 5);       // text panel of a sold row
    static bool FadedCell(int p) => Is(p, 247, 246, 244, 5);   // icon cell of a sold row

    /// <summary>
    /// The listing rows along a panel edge: runs of listing height (26 to 40 px). Rows still for sale are blue; sold
    /// rows (greyed out, the owner's view) are near-white with a pale icon cell.
    /// </summary>
    static (List<(int top, int h)> active, List<(int top, int h)> sold) Rows(Frame f, int x)
    {
        var active = new List<(int, int)>();
        var sold = new List<(int, int)>();
        int run = 0, fade = 0, cell = 0;
        for (int y = 60; y < f.H - 20; y++)
        {
            bool blue = Blue(f.At(x, y)) && Blue(f.At(x + 1, y));
            bool grey = !blue && Faded(f.At(x, y)) && Faded(f.At(x + 1, y));
            if (blue) run++; else { if (run >= 26 && run <= 40) active.Add((y - run, run)); run = 0; }
            // the faded icon can reach into the sampled cell column, so most (not all) of the row must show the pale cell
            if (grey) { fade++; if (FadedCell(f.At(x - 6, y))) cell++; }
            else { if (fade >= 26 && fade <= 40 && cell >= fade * 2 / 3) sold.Add((y - fade, fade)); fade = 0; cell = 0; }
        }
        // Sold rows only count between or beside rows of the same list (not some other pale window).
        if (active.Count > 0)
        {
            int lo = active.Min(r => r.Item1) - 6 * 40, hi = active.Max(r => r.Item1) + 6 * 40;
            sold = sold.Where(r => r.Item1 > lo && r.Item1 < hi).ToList();
        }
        else sold.Clear();
        return (active, sold);
    }

    /// <summary>
    /// The catalog items a listing's name can stand for. A long name is cut off in the list ("Gloves Attack Scroll:
    /// Inter…"), and the dots are often not read, so a name that is the start of catalog names counts too.
    /// </summary>
    static List<CatalogItem> Candidates(Vision vision, string name)
    {
        var key = NameKey(name);
        if (key.Length < 3) return new();
        var candidates = vision.Catalog.Values.Where(c => NameKey(c.name) == key).ToList();
        if (candidates.Count == 0 && key.Length >= 10)
        {
            candidates = vision.Catalog.Values.Where(c => NameKey(c.name).StartsWith(key, StringComparison.Ordinal)).ToList();
            // the cut can leave a sliver of the next letter, read as a stray character ("Intern" for "Interm…")
            if (candidates.Count == 0) candidates = vision.Catalog.Values.Where(c => NameKey(c.name).StartsWith(key[..^1], StringComparison.Ordinal)).ToList();
        }
        // The text reader slips a letter now and then ("Fragment ot Magic"): allow near misses.
        if (candidates.Count == 0 && key.Length >= 6)
            candidates = vision.Catalog.Values.Where(c => Distance(NameKey(c.name), key) <= (key.Length >= 12 ? 2 : 1)).ToList();
        return candidates;
    }

    static string NameKey(string value) => Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9]", "");

    // OCR slips that matter inside numbers.
    static string Normalise(string s) => s.Replace('.', ',').Replace('g', '9').Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1').Replace("f0r", "for").Replace("mes0s", "mesos");
    static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    /// <summary>Edit distance, or 99 when the lengths differ by more than 2.</summary>
    internal static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 99;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>
    /// Characters drawn on a listing's price line (its lower part), with MapleLite's ink counter: black lettering for
    /// listings for sale (ink below 100), grey for sold ones (below 200).
    /// </summary>
    static int PriceGlyphs(Frame f, int panelX, int top, int h, int ink) =>
        ImageOps.CountInkRuns(f, new Region(panelX + 2, top + 18, PanelW - 3, h - 19), p => Pixel.Max(p) < ink);
}


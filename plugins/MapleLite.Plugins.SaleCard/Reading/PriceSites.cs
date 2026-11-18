using System.Text.RegularExpressions;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;
using MapleLite.Sdk;

namespace MapleLite.Plugins.SaleCard;

/// <summary>What one price page showed: the site, its figures per item, and a note for the status line.</summary>
public sealed record PricePage(string Site, IReadOnlyList<SiteRead> Reads, string Note);

/// <summary>
/// Somewhere prices come from. Today: price pages open in the user's own browser (opt-in). Next: MapleLite's
/// built-in browser. Sources only look at pages the user opened; they never load or refresh a site themselves.
/// </summary>
public interface IPriceSource
{
    string Name { get; }
    /// <summary>The pages showing right now, read once. Empty with a note when there is nothing to read.</summary>
    Task<(IReadOnlyList<PricePage> pages, string note)> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Reads price pages showing in the user's own browser windows (Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Safari,
/// Arc), only from pictures of those windows: the site gets no requests and the browser no input.
/// Window titles are only matched against the price sites' names; nothing else about other windows is kept.
/// </summary>
public sealed class BrowserTabSource(IOcr ocr, IOtherWindows windows) : IPriceSource
{
    static readonly string[] Browsers = { "chrome", "msedge", "microsoft edge", "firefox", "brave", "opera", "vivaldi", "safari", "arc" };

    public string Name => "Browser tabs";

    public async Task<(IReadOnlyList<PricePage> pages, string note)> ReadAsync(CancellationToken ct)
    {
        var found = windows.List()
            .Select(w => (w, site: PriceSites.SiteOf(w.Title)))
            .Where(x => x.site != null && Browsers.Any(b => x.w.ProcessName.Contains(b, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (found.Count == 0) return ([], "no price site tab showing");
        var pages = new List<PricePage>();
        foreach (var (w, site) in found)
        {
            if (w.Minimized) { pages.Add(new PricePage(site!, [], "minimized")); continue; }
            var frame = await windows.CaptureAsync(w.Handle, ct);
            if (frame == null || frame.W < 200 || frame.H < 200) { pages.Add(new PricePage(site!, [], "couldn't read the window")); continue; }
            var (reads, debug) = await PriceSites.ReadAsync(ocr, frame, site!, w.Title);
            pages.Add(new PricePage(site!, reads, debug));
        }
        return (pages, "");
    }
}

/// <summary>
/// Reads prices off the price sites' pages from a picture. Each site has its own layout: MesoWatch's Item Info panel
/// and list, Cozy Market's item page and list, NiaMeowDB's Free Market section, maplestory-classic.com's price tile
/// and list. Ported from the Sale Card Generator app; the text comes from MapleLite's shared OCR.
/// </summary>
public static class PriceSites
{
    public const string Meso = "meso.watch", Cozy = "cozy.markets", Nia = "niadb", Classic = "maplestory-classic.com";
    public static readonly string[] All = { Meso, Cozy, Nia, Classic };

    /// <summary>
    /// The sites this reader understands, with the words in a tab title that mark each. NiaMeowDB's titles also say
    /// "MapleStory Classic", so it is checked first, and maplestory-classic.com is only taken on its price pages,
    /// whose titles all say "Free Market price".
    /// </summary>
    static readonly (string site, string title, string also)[] Sites =
        { (Meso, "MesoWatch", ""), (Cozy, "Cozy Market", ""), (Nia, "NiaMeowDB", ""), (Classic, "MapleStory Classic", "Free Market Price") };

    public static string? SiteOf(string title) =>
        Sites.Where(s => title.Contains(s.title, StringComparison.OrdinalIgnoreCase) && title.Contains(s.also, StringComparison.OrdinalIgnoreCase)).Select(s => s.site).FirstOrDefault();

    static Task<IReadOnlyList<OcrLine>> Read(IOcr ocr, Frame f, int x, int y, int w, int h, int scale, int pad = 0) => ocr.TryReadAsync(f, x, y, w, h, scale, pad);

    public static async Task<(List<SiteRead> reads, string debug)> ReadAsync(IOcr ocr, Frame f, string site, string title = "")
    {
        var lines = (await ocr.TryReadAsync(f, new OcrRequest(f.Bounds) { Scale = f.W <= 2000 ? 2 : 1, Scaling = OcrScaling.Smooth })).ToList();
        var reads = new List<SiteRead>();
        string debug = "";
        if (site == Meso)
        {
            var (panel, why) = await Panel(ocr, f, lines);
            if (panel != null) reads.Add(panel);
            debug = why;
            reads.AddRange(await Table(ocr, f, lines, "TYPICAL", 45, false, 12, darkFigures: true));
        }
        else if (site == Cozy)
        {
            var (panel, why) = await CozyItem(ocr, f, lines);
            if (panel != null) reads.Add(panel);
            debug = why;
            reads.AddRange(await Table(ocr, f, lines, "USUAL PRICE", 78, true, 12));
        }
        else if (site == Classic)
        {
            var (panel, why) = await ClassicItem(ocr, f, lines, title);
            if (panel != null) reads.Add(panel);
            debug = why;
            // the list's figures are right-aligned well past the "Price" heading
            reads.AddRange(await Table(ocr, f, lines, "PRICE", 32, false, 40));
            if (reads.Count > 0) debug = "";
        }
        else if (site == Nia)
        {
            var (panel, why) = await NiaItem(ocr, f, lines, title);
            if (panel != null) reads.Add(panel);
            debug = why.Length > 0 ? why : panel == null ? "scroll to the item's Free Market section" : "";
        }
        if (reads.Count == 0 && debug.Length == 0) debug = "no price list showing";
        return (reads, debug);
    }

    /// <summary>MesoWatch's Item Info panel: a figure with "LAST SALE", "LAST 3 AVG" or "LAST 10 MEDIAN" under it.</summary>
    static async Task<(SiteRead? read, string debug)> Panel(IOcr ocr, Frame f, List<OcrLine> lines)
    {
        var labels = new List<(string kind, double left, double right, double y)>();
        foreach (var l in lines)
        {
            string up = l.Text.ToUpperInvariant();
            if (!up.Contains("LAST")) continue;
            var inLine = new[] { ("last", "SALE"), ("avg", "AVG"), ("median", "MEDIAN") }
                .Select(k => (kind: k.Item1, at: up.IndexOf(k.Item2, StringComparison.Ordinal))).Where(k => k.at >= 0).OrderBy(k => k.at).ToList();
            // one line normally holds one label; if the reader joined them, share the line's width between them
            for (int i = 0; i < inLine.Count; i++)
                labels.Add((inLine[i].kind, l.Left() + (l.Right() - l.Left()) * i / inLine.Count, l.Left() + (l.Right() - l.Left()) * (i + 1) / inLine.Count, l.Cy()));
        }
        if (labels.Count == 0) return (null, "");
        double labelY = labels.Average(l => l.y);
        if (labels.Any(l => Math.Abs(l.y - labelY) > 12) || labels.Select(l => l.kind).Distinct().Count() != labels.Count)
            return (null, "price tiles unclear");

        // Each figure is read on its own, enlarged: the whole-window pass drops some of them.
        var values = new Dictionary<string, long>();
        foreach (var lab in labels)
        {
            int x0 = (int)lab.left - 30, w = (int)(lab.right - lab.left) + 60, y0 = (int)lab.y - 45;
            var tile = await Read(ocr, f, x0, y0, w, 38, 3, 4);
            var best = tile.Select(t => (cy: t.Cy(), p: Prices(t.Text))).Where(t => t.p.Count > 0).OrderByDescending(t => t.cy).FirstOrDefault();
            if (best.p == null) return (null, $"couldn't read the {lab.kind} figure");
            values[lab.kind] = best.p[^1];
        }

        // The item's name sits just above its "N listings · N sales" line...
        string? name = null;
        var info = lines.Where(l => l.Text.Contains("listing", StringComparison.OrdinalIgnoreCase) && l.Text.Contains("sale", StringComparison.OrdinalIgnoreCase)
                && l.Cy() < labelY && l.Left() > labels.Min(x => x.left) - 80)
            .OrderByDescending(l => l.Cy()).FirstOrDefault();
        if (info != null)
        {
            // A long name wraps ("Overall Armor STR Scroll:" / "Greater"): take every line of it, from the one just
            // above the listings line upwards, while the lines stay close together and start at the same place.
            var above = lines.Where(l => l.Cy() < info.Cy() - 4 && l.Cy() > info.Cy() - 110 && Math.Abs(l.Left() - info.Left()) < 60)
                .OrderByDescending(l => l.Cy()).ToList();
            var title = new List<OcrLine>();
            foreach (var l in above)
            {
                double from = title.Count == 0 ? info.Cy() : title[^1].Cy();
                if (from - l.Cy() > 34 || title.Count == 3) break;
                title.Add(l);
            }
            if (title.Count > 0) name = string.Join(" ", title.AsEnumerable().Reverse().Select(l => l.Text.Trim()));
        }
        // ...and when the panel is scrolled past it, the Recent sales list under the tiles repeats it.
        name ??= lines.Where(l => l.Cy() > labelY + 30 && l.Left() > labels.Min(x => x.left) - 40 && l.Text.Any(char.IsLetter))
            .GroupBy(l => l.Text.Trim()).Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        if (string.IsNullOrWhiteSpace(name)) return (null, "scroll the Item Info panel up so the item's name shows");
        long Of(string kind) => values.TryGetValue(kind, out long v) ? v : 0;
        return (new SiteRead(new[] { name.Trim() }, false, Of("last"), Of("avg"), Of("median")), "");
    }

    /// <summary>
    /// NiaMeowDB's item page, Free Market section: a "LAST 24 HOURS" label with the median asking price under it,
    /// and a "Sold for ~N" line. The item's name is the start of the tab title.
    /// </summary>
    static async Task<(SiteRead? read, string debug)> NiaItem(IOcr ocr, Frame f, List<OcrLine> lines, string title)
    {
        int cut = title.IndexOf(" - MapleStory Classic", StringComparison.OrdinalIgnoreCase);
        if (cut <= 0) return (null, "open an item's page");
        string name = title[..cut].Trim();
        var label = lines.FirstOrDefault(l => Regex.IsMatch(l.Text.Trim(), @"^LAST\s+\d+\s+(HOURS?|DAYS?)", RegexOptions.IgnoreCase));
        if (label == null) return (null, "");
        long typical = lines.Where(l => l.Cy() > label.Cy() + 6 && l.Cy() < label.Cy() + 50 && Math.Abs(l.Left() - label.Left()) < 30)
            .OrderBy(l => l.Cy()).SelectMany(l => Prices(l.Text).Take(1)).FirstOrDefault();
        if (typical == 0)
        {
            int x0 = Math.Max(0, (int)label.Left() - 10), y0 = (int)label.Cy() + 8, w = Math.Min(150, f.W - x0), h = Math.Min(40, f.H - y0);
            if (w > 20 && h > 20) typical = (await Read(ocr, f, x0, y0, w, h, 3, 6)).SelectMany(l => Prices(l.Text).Take(1)).FirstOrDefault();
        }
        var soldLine = lines.FirstOrDefault(l => l.Cy() > label.Cy() && l.Cy() < label.Cy() + 110 && l.Text.TrimStart().StartsWith("Sold for", StringComparison.OrdinalIgnoreCase));
        long sold = soldLine != null ? Prices(soldLine.Text).FirstOrDefault() : 0;
        if (typical == 0 && sold == 0) return (null, "couldn't read the item's figures");
        // "Sold for" is the median of recent sales: it stands in for the last-10-median figure
        return (new SiteRead(new[] { name }, false, Median10: sold, Typical: typical), "");
    }

    /// <summary>
    /// maplestory-classic.com's item page: a dark "PRICE" tile with the median sale under it. The item's name is the
    /// start of the tab title ("Iron Ore Free Market price | MapleStory Classic").
    /// </summary>
    static async Task<(SiteRead? read, string debug)> ClassicItem(IOcr ocr, Frame f, List<OcrLine> lines, string title)
    {
        // only an item's page has a title of this shape; the list's is "MapleStory Classic Free Market Prices: ..."
        var m = Regex.Match(title, @"^(.+?) Free Market price \| MapleStory Classic", RegexOptions.IgnoreCase);
        if (!m.Success) return (null, "");
        string name = m.Groups[1].Value.Trim();
        var label = lines.FirstOrDefault(l => l.Text.Trim().Equals("PRICE", StringComparison.OrdinalIgnoreCase));
        if (label == null) return (null, "scroll so the item's Price tile shows");
        // light lettering on a dark tile: keep only the light fill
        int x0 = Math.Max(0, (int)label.Left() - 8), y0 = (int)label.Cy() + 8, w = Math.Min(170, f.W - x0), h = Math.Min(40, f.H - y0);
        if (w < 20 || h < 20) return (null, "scroll so the item's Price tile shows");
        long price = (await Read(ocr, LightText(f, x0, y0, w, h), 0, 0, w, h, 3, 6)).SelectMany(l => Prices(l.Text).Take(1)).FirstOrDefault();
        if (price == 0) return (null, "couldn't read the item's price: close anything covering the Price tile (the site's email pop-up sits on it)");
        // the median of recent sales: it is the site's usual figure and stands in for the last-10-median one too
        return (new SiteRead(new[] { name }, false, Median10: price, Typical: price), "");
    }

    /// <summary>Where a word sits along a line of text, by its share of the line's letters.</summary>
    static (double left, double right)? Span(OcrLine l, string word)
    {
        string up = l.Text.ToUpperInvariant();
        int at = up.IndexOf(word, StringComparison.Ordinal);
        if (at < 0) return null;
        double cw = (l.Right() - l.Left()) / Math.Max(1, up.Length);
        return (l.Left() + at * cw, l.Left() + (at + word.Length) * cw);
    }

    /// <summary>Cozy Market's item page: a row of labels (FM USUAL ... LAST SOLD) with each figure under its label.</summary>
    static async Task<(SiteRead? read, string debug)> CozyItem(IOcr ocr, Frame f, List<OcrLine> lines)
    {
        var sold = lines.Select(l => (l, s: Span(l, "LAST SOLD"))).FirstOrDefault(x => x.s != null);
        if (sold.l == null) return (null, "");
        var usual = lines.Where(l => Math.Abs(l.Cy() - sold.l.Cy()) < 12).Select(l => Span(l, "FM USUAL")).FirstOrDefault(s => s != null);

        async Task<long> Under((double left, double right) s)
        {
            int x0 = Math.Max(0, (int)s.left - 45), w = Math.Min(f.W - x0, (int)(s.right - s.left) + 60), y0 = (int)sold.l.Cy() + 8, h = Math.Min(40, f.H - y0);
            if (w < 20 || h < 20) return 0;
            // the figures are light outlined text, except the usual price, which is dark on a gold pill: try both ways
            foreach (bool light in new[] { true, false })
            {
                var got = light ? await Read(ocr, LightText(f, x0, y0, w, h), 0, 0, w, h, 3, 6) : await Read(ocr, f, x0, y0, w, h, 3, 6);
                var p = got.SelectMany(g => Prices(g.Text)).ToList();
                if (p.Count > 0) return p[^1];
            }
            return 0;
        }
        long last = await Under(sold.s!.Value), typical = usual != null ? await Under(usual.Value) : 0;
        if (last == 0 && typical == 0) return (null, "couldn't read the item's figures");

        // The name is the big line at the top, above the "NPC pays ... Times listed" line.
        var npc = lines.Where(l => l.Cy() < sold.l.Cy() && (l.Text.Contains("NPC pays", StringComparison.OrdinalIgnoreCase) || l.Text.Contains("Times listed", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(l => l.Left()).FirstOrDefault();
        if (npc == null) return (null, "scroll up so the item's name shows");
        int nx = Math.Max(0, (int)npc.Left() - 12), ny = Math.Max(0, (int)npc.Cy() - 72), nw = f.W - nx, nh = Math.Min(40, f.H - ny);
        if (nw < 20 || nh < 20) return (null, "scroll up so the item's name shows");
        var name = (await Read(ocr, LightText(f, nx, ny, nw, nh), 0, 0, nw, nh, 2, 6)).Where(n => n.Text.Count(char.IsLetter) >= 3).OrderBy(n => n.Cy()).FirstOrDefault();
        if (name == null) return (null, "couldn't read the item's name");
        return (new SiteRead(new[] { name.Text.Trim() }, false, Last: last, Typical: typical), "");
    }

    /// <summary>Light, outlined lettering on a dark page: keep only the light fill, as black on white, which reads far better.</summary>
    static Frame LightText(Frame f, int x0, int y0, int w, int h)
    {
        var o = new Frame(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = f.At(x0 + x, y0 + y);
                int lo = Math.Min((p >> 16) & 255, Math.Min((p >> 8) & 255, p & 255));
                int v = 255 - Math.Clamp((lo - 150) * 255 / 70, 0, 255);
                o.Px[y * w + x] = unchecked((int)0xFF000000) | v << 16 | v << 8 | v;
            }
        return o;
    }

    /// <summary>
    /// A price list: a header row with "ITEM" and a price column, then one row per item. The name column and
    /// the price column are each read on their own, enlarged, and paired up by height on the page.
    /// </summary>
    static async Task<List<SiteRead>> Table(IOcr ocr, Frame f, List<OcrLine> lines, string priceWord, int nameOffset, bool lightNames, int pastHeading, bool darkFigures = false)
    {
        var reads = new List<SiteRead>();
        // The headings are small and pale: on a tall page the whole-page read can find "ITEM" and miss the price
        // heading beside it. Then the heading row is read again on its own, enlarged.
        if (!lines.Any(l => l.Text.ToUpperInvariant().Contains(priceWord)))
            foreach (var itemHead in lines.Where(l => Regex.IsMatch(l.Text, @"^\s*ITEM\b", RegexOptions.IgnoreCase)).ToList())
            {
                var band = await Read(ocr, f, 0, Math.Max(0, (int)itemHead.Cy() - 12), f.W, 24, 3, 4);
                if (band.Any(l => l.Text.ToUpperInvariant().Contains(priceWord))) { lines = lines.Concat(band).ToList(); break; }
            }
        foreach (var head in lines.Where(l => l.Text.ToUpperInvariant().Contains(priceWord)))
        {
            var row = lines.Where(l => Math.Abs(l.Cy() - head.Cy()) < 12).ToList();
            var item = row.Select(l => Span(l, "ITEM")).FirstOrDefault(c => c != null);
            var price = Span(head, priceWord);
            if (item == null || price == null || price.Value.left < item.Value.left + 120) continue;
            // the name column ends where the next column (KIND, if the list has one) or the price column starts
            var kind = row.Select(l => Span(l, "KIND")).FirstOrDefault(c => c != null && c.Value.left > item.Value.left + 80);
            int top = (int)head.Cy() + 12, h = f.H - top;
            int nx0 = (darkFigures ? NameStart(lines, head, item.Value.left, price.Value.left) : null) ?? (int)item.Value.left + nameOffset, nx1 = (int)(kind?.left ?? price.Value.left) - 6;
            if (h < 40 || nx1 - nx0 < 60 || nx0 < 0) continue;
            nx1 = Math.Min(nx1, f.W - 1);
            IReadOnlyList<OcrLine> names;
            if (lightNames)
                // read off a separate black-on-white picture: move the boxes back onto the page
                names = (await Read(ocr, LightText(f, nx0, top, nx1 - nx0, h), 0, 0, nx1 - nx0, h, 3, 6))
                    .Select(n => n with { Box = n.Box with { X = n.Box.X + nx0, Y = n.Box.Y + top } }).ToList();
            else names = await Read(ocr, f, nx0, top, nx1 - nx0, h, 2);
            // the prices are read with the columns beside them in view (a lone narrow strip reads worse), then kept by column
            int px0 = Math.Max(0, (int)price.Value.left - 45), px1 = Math.Min(f.W, (int)price.Value.right + pastHeading);   // figures line up with the heading's right edge; the next column starts just past it
            int wx0 = Math.Max(0, px0 - 220), wx1 = Math.Min(f.W, px1 + 220);
            var prices = (await Read(ocr, f, wx0, top, wx1 - wx0, h, 3))
                // a line can run across several columns ("600K +6%"): keep only the words standing in the price column
                .Select(p => (p, ws: p.Words.Where(w => w.Box.CenterX > px0 && w.Box.CenterX < px1).ToList()))
                .Where(t => t.ws.Count > 0)
                .Select(t => OcrLine.FromWords(t.ws)).ToList();
            names = names.Select(n => n with { Text = CleanName(n.Text) }).Where(n => n.Text.Count(char.IsLetter) >= 2).ToList();
            var used = new HashSet<OcrLine>();
            foreach (var p in prices)
            {
                if (Prices(p.Text).Count == 0) continue;
                var mine = names.Where(n => Math.Abs(n.Cy() - p.Cy()) <= 20).OrderBy(n => n.Cy()).ToList();
                if (mine.Count == 0) continue;
                // the figure has to agree with the picture: re-read the cell until it does, or leave the row out
                long v = darkFigures ? await CheckedFigure(ocr, f, p.Text, px0, px1, (int)p.Cy()) : Prices(p.Text)[^1];
                if (v <= 0) continue;
                used.UnionWith(mine);
                reads.Add(new SiteRead(mine.Select(n => n.Text).ToArray(), mine.Any(n => n.Right() >= nx1 - 14), Typical: v));
            }
            // rows whose figure the column read missed altogether: read their cell on its own
            if (darkFigures)
                foreach (var n in names.Where(n => !used.Contains(n)).OrderBy(n => n.Cy()).ToList())
                {
                    if (used.Contains(n) || names.Any(o => o != n && o.Cy() < n.Cy() && n.Cy() - o.Cy() < 26)) continue;   // the item's first line only
                    var mine = names.Where(o => o.Cy() >= n.Cy() && o.Cy() - n.Cy() < 26).ToList();
                    long v = await CheckedFigure(ocr, f, "", px0, px1, (int)n.Cy() + 6);
                    if (v <= 0) continue;
                    used.UnionWith(mine);
                    reads.Add(new SiteRead(mine.Select(o => o.Text).ToArray(), mine.Any(o => o.Right() >= nx1 - 14), Typical: v));
                }
            break;
        }
        return reads;
    }

    /// <summary>
    /// Where a list's names start: just left of the names the whole-page read found under the heading. A fixed
    /// offset from "ITEM" cuts the first letter off at some widths and zoom levels ("ron Ore").
    /// </summary>
    static int? NameStart(List<OcrLine> lines, OcrLine head, double itemLeft, double priceLeft)
    {
        var lefts = lines.Where(l => l.Cy() > head.Cy() + 12 && l.Left() > itemLeft - 10 && l.Left() < priceLeft - 80 && l.Text.Count(char.IsLetter) >= 3)
            .Select(l => l.Left()).OrderBy(x => x).ToList();
        if (lefts.Count < 3) return null;
        return Math.Max((int)itemLeft, (int)lefts[lefts.Count / 4] - 10);
    }

    /// <summary>A name as read, without what the icon or star beside it left in front (a stray mark or lone letter).</summary>
    static string CleanName(string text)
    {
        var t = Regex.Replace(text.Trim(), @"^[^A-Za-z0-9(]+", "");
        return Regex.Replace(t, @"^[A-Za-z0-9]\s+(?=[A-Z])", "").Trim();
    }

    /// <summary>The last figure in a line of text, as written ("3,500", "70K") and as a number.</summary>
    static (string text, long value)? LastFigure(string line)
    {
        // the same clean-up as Prices: the coin read as a stray "0", the "about" sign stuck to the figure
        var text = Regex.Replace(line.Trim(), @"^[^\d]*?[0Oo]\s+(?=[~\-]?\d)", "");
        text = Regex.Replace(text, @"(?<![A-Za-z\d])[N~\-—–∼]+(?=\d)", " ");
        var m = Price.Matches(text).LastOrDefault();
        if (m == null) return null;
        var v = Prices(m.Value);
        return v.Count == 0 ? null : (Regex.Replace(m.Value, @"\s", ""), v[^1]);
    }

    /// <summary>
    /// A list figure checked against the picture: the dark characters drawn in its cell are counted (the gold coin
    /// before it isn't dark, so it doesn't count) and a reading is kept only when it has that many characters. The
    /// column read is tried first; then the cell is read on its own, several ways. Zero when nothing agrees: a row
    /// left out is better than a wrong price ("90" read as "9").
    /// </summary>
    static async Task<long> CheckedFigure(IOcr ocr, Frame f, string columnText, int x0, int x1, int cy)
    {
        static bool Dark(int p) => Pixel.Max(p) < 100;
        var cell = new Region(x0, cy - 12, x1 - x0, 24).Intersect(f.Bounds);
        if (cell.Width < 10 || cell.Height < 10) return 0;
        // count from just past the gold coin: at some zoom levels its dark rim would count as a character
        static bool Gold(int p) => ((p >> 16) & 255) > 170 && ((p >> 8) & 255) > 120 && (p & 255) < 110 && ((p >> 16) & 255) - (p & 255) > 90;
        int coinRight = -1;
        for (int x = cell.X; x < cell.Right; x++)
            for (int y = cell.Y; y < cell.Bottom; y++)
                if (Gold(f.At(x, y))) { coinRight = x; break; }
        if (coinRight >= 0 && coinRight + 3 < cell.Right) cell = new Region(coinRight + 3, cell.Y, cell.Right - coinRight - 3, cell.Height);
        int glyphs = ImageOps.CountInkRuns(f, cell, Dark);
        var first = LastFigure(columnText);
        if (glyphs == 0) return first?.value ?? 0;   // no dark lettering (another theme): nothing to check against
        // a figure never starts with 0: "09" is the coin read as a digit with the real first digit lost
        static bool Fits((string text, long value)? x, int n) => x is { } y && y.text.Length == n && !(y.text.Length > 1 && y.text[0] == '0' && char.IsDigit(y.text[1]));
        if (Fits(first, glyphs)) return first!.Value.value;
        var crops = new[] { cell, new Region(x0, cy - 15, x1 - x0, 30).Intersect(f.Bounds) };
        var (hit, _) = await ocr.ReadUntilAsync(f, OcrBlocks.Variants(crops), got =>
            got.Select(l => LastFigure(l.Text)).FirstOrDefault(x => Fits(x, glyphs)) is { } ok ? ok.value.ToString() : null);
        return hit != null ? long.Parse(hit) : 0;
    }

    static readonly Regex Price = new(@"(?<![A-Za-z\d])(\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s?([KMBkmb])?(?!\s?%)(?![A-Za-z\d])");

    /// <summary>Every amount in a line of text: "600K" is 600000, "1.2m" 1200000, "12,345" 12345.</summary>
    public static List<long> Prices(string text)
    {
        // the coin drawn before a figure reads as a stray "0", "O" or "o"
        text = Regex.Replace(text.Trim(), @"^[^\d]*?[0Oo]\s+(?=[~\-]?\d)", "");
        // the "about" sign before a figure reads as N, a dash or a tilde stuck to it
        text = Regex.Replace(text, @"(?<![A-Za-z\d])[N~\-—–∼]+(?=\d)", " ");
        var found = new List<long>();
        foreach (Match m in Price.Matches(text))
        {
            if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v)) continue;
            double mul = m.Groups[2].Value.ToUpperInvariant() switch { "K" => 1e3, "M" => 1e6, "B" => 1e9, _ => 1 };
            if (v * mul >= 1) found.Add((long)Math.Round(v * mul));
        }
        return found;
    }
}

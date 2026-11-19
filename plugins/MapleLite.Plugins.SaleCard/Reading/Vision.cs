using MapleLite.Core.Game;
using System.Text;
using System.Text.Json;
using MapleLite.Core.Imaging;
using MapleLite.Sdk;

namespace MapleLite.Plugins.SaleCard;

public sealed record InvLayout(int GridX, int GridY, int Tab);

public sealed class SlotRead
{
    public int Col, Row;
    public int[] Pixels = Array.Empty<int>();   // 32x32 crop
    public int ItemId;                           // 0 = not recognized
    public double Score;
    public int Quantity = 1;
    public bool QuantityKnown = true;
    public bool HasCount;
}

/// <summary>
/// Finds the inventory and storage grids in a game picture and recognises the items in them by their icons
/// (the shared item catalog's icons, matched pixel by pixel). Ported unchanged from the Sale Card Generator app.
/// </summary>
public sealed class Vision
{
    public const int Slot = 32, PitchX = 36, PitchY = 34, Cols = 5, Rows = 6;
    public static readonly string[] TabNames = { "Equip", "Use", "Set Up", "Etc", "Cash", "Deco" };

    sealed class Template
    {
        public int Id, W, H, Opaque;
        public int[] Px = Array.Empty<int>();
    }

    readonly List<Template> _templates = new();
    public Dictionary<int, CatalogItem> Catalog { get; } = new();
    readonly ItemCatalog _items;

    /// <summary>Builds the icon templates from the shared item catalog (context.Items).</summary>
    public Vision(ItemCatalog items)
    {
        _items = items;
        foreach (var it in items.Items) Catalog[it.id] = it;
        var iconDir = items.IconFolder;
        if (iconDir == null || !Directory.Exists(iconDir)) return;
        Parallel.ForEach(Directory.EnumerateFiles(iconDir, "*.png"), file =>
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) return;
            try
            {
                var f = FrameFiles.LoadPng(file, opaque: false);
                if (f.W > 40 || f.H > 40 || f.W < 4 || f.H < 4) return;
                var t = new Template { Id = id, W = f.W, H = f.H, Px = f.Px };
                t.Opaque = f.Px.Count(p => (p >>> 24) >= 250);
                if (t.Opaque >= 40) lock (_templates) _templates.Add(t);
            }
            catch { /* unreadable icon: skip */ }
        });
        _templates.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    public int TemplateCount => _templates.Count;
    /// <summary>The item's picture, or null when it has none.</summary>
    public string? IconPath(int id) => _items.IconPath(id);
    public string Name(int id) => Catalog.TryGetValue(id, out var c) ? c.name : "";

    // ---------------------------------------------------------------- an item's grade by its icon's colours

    const int Bins = 4;   // levels per colour channel: 64 colour boxes in all

    /// <summary>The share of an icon's pixels in each colour box. Near-black outline pixels don't count.</summary>
    static float[]? Colours(IEnumerable<int> pixels)
    {
        var h = new float[Bins * Bins * Bins];
        int n = 0;
        foreach (int p in pixels)
        {
            if (Math.Max(R(p), Math.Max(G(p), B(p))) < 50) continue;
            h[R(p) * Bins / 256 * Bins * Bins + G(p) * Bins / 256 * Bins + B(p) * Bins / 256]++;
            n++;
        }
        if (n < 20) return null;
        for (int i = 0; i < h.Length; i++) h[i] /= n;
        return h;
    }

    readonly Dictionary<int, float[]?> _iconColours = new();

    /// <summary>The colours of an item's own icon (its drawn pixels only).</summary>
    float[]? IconColours(int id)
    {
        lock (_iconColours)
        {
            if (_iconColours.TryGetValue(id, out var c)) return c;
            var t = _templates.Find(x => x.Id == id);
            return _iconColours[id] = t == null ? null : Colours(t.Px.Where(p => (p >>> 24) >= 250));
        }
    }

    /// <summary>
    /// The colours of an icon as drawn on a page, inside its box. The box's own fill is what runs along the edge of
    /// the picture: those colours are left out, so what is left is the icon.
    /// </summary>
    static float[]? PageColours(Frame icon)
    {
        var edge = new List<int>();
        for (int x = 0; x < icon.W; x++) { edge.Add(icon.At(x, 0)); edge.Add(icon.At(x, icon.H - 1)); }
        for (int y = 1; y < icon.H - 1; y++) { edge.Add(icon.At(0, y)); edge.Add(icon.At(icon.W - 1, y)); }
        // the fill colours: those making up a good part of the edge
        var fills = edge.GroupBy(p => (R(p) / 16, G(p) / 16, B(p) / 16)).Where(g => g.Count() >= edge.Count / 10).Select(g => g.First()).ToList();
        return Colours(icon.Px.Where(p => !fills.Any(f => Near(p, f, 24))));
    }

    /// <summary>How alike two colour make-ups are, 0 (nothing shared) to 1 (the same).</summary>
    static double Overlap(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Min(a[i], b[i]);
        return s;
    }

    static string Key(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToUpperInvariant(), "[^A-Z0-9]", "");

    /// <summary>
    /// Which item a list row stands for when the list names it without its grade ("Shoes Speed Scroll" for the
    /// Lesser, Intermediate, Greater and Chaos ones): the items named that plus one more word, told apart by the
    /// row's icon. 0 unless one of them clearly matches the icon.
    /// </summary>
    public int ByIcon(string name, Frame? icon)
    {
        if (icon == null) return 0;
        var scored = IconScores(name, icon);
        if (scored.Count == 0) return 0;
        double next = scored.Count > 1 ? scored[1].score : 0;
        return scored[0].score >= MinIconOverlap && scored[0].score - next >= MinIconLead ? scored[0].id : 0;
    }

    internal const double MinIconOverlap = 0.4, MinIconLead = 0.15;

    /// <summary>
    /// Each candidate's likeness to the row's icon, best first. The grades' icons share most of their colours (the
    /// same paper): only what sets each apart is compared, as the share of it found in the row's icon.
    /// </summary>
    public IReadOnlyList<(int id, double score)> IconScores(string name, Frame icon)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string key = Key(name);
        if (key.Length < 6 || words.Length < 2) return [];
        var page = PageColours(icon);
        if (page == null) return [];
        var cands = Catalog.Values
            .Where(c => { var cw = c.name.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries); return cw.Length == words.Length + 1 && Key(string.Join("", cw.Take(words.Length))) == key; })
            .Select(c => (c.id, colours: IconColours(c.id))).Where(c => c.colours != null).ToList();
        if (cands.Count == 0) return [];
        if (cands.Count == 1) return [(cands[0].id, Overlap(page, cands[0].colours!))];
        // the colours every candidate has (in the smallest share any of them has it) are left out of each
        var shared = new float[page.Length];
        for (int i = 0; i < shared.Length; i++) shared[i] = cands.Min(c => c.colours![i]);
        var pageOwn = page.Select((v, i) => Math.Max(0, v - shared[i])).ToArray();
        return cands.Select(c =>
        {
            double own = 0, found = 0;
            for (int i = 0; i < page.Length; i++)
            {
                double d = c.colours![i] - shared[i];
                own += d;
                found += Math.Min(d, pageOwn[i]);
            }
            return (c.id, score: own > 0.02 ? found / own : 0);
        }).OrderByDescending(x => x.score).ToList();
    }

    static int R(int p) => (p >> 16) & 255;
    static int G(int p) => (p >> 8) & 255;
    static int B(int p) => p & 255;
    static bool Near(int a, int b, int tol) =>
        Math.Abs(R(a) - R(b)) <= tol && Math.Abs(G(a) - G(b)) <= tol && Math.Abs(B(a) - B(b)) <= tol;

    /// <summary>Finds the inventory window by the red bar under its tabs.</summary>
    public static InvLayout? Locate(Frame f)
    {
        // The bar is 203 px wide. Another game window (storage, shop) may cover its left end, so the
        // grid is measured from the bar's right end, and a shortened bar is still accepted.
        const int BarW = 203;
        InvLayout? fallback = null;
        int storageBarY = LocateStorage(f) is { } st ? st.GridY - StorageDy : -100;
        for (int y = 20; y < f.H - 220; y++)
        {
            // The storage window shows a second copy of the inventory on the same line as its own bar: not the real one.
            if (Math.Abs(y - storageBarY) <= 2) continue;
            int run = 0;
            for (int x = 0; x <= f.W; x++)
            {
                bool red = false;
                if (x < f.W) { int p = f.At(x, y); red = R(p) > 185 && G(p) < 70 && B(p) < 80; }
                if (red) { run++; continue; }
                if (run >= 150 && run <= BarW + 1 && x - BarW >= 0 && Confirm(f, x - BarW, y, out var lay))
                {
                    if (lay!.Tab >= 0) return lay;   // a lit tab above it: certainly the inventory
                    fallback ??= lay;
                }
                run = 0;
            }
        }
        return fallback;
    }

    const int StorageDx = 5, StorageDy = 10;
    const int SlotColour = 0xE1DFD5;   // the empty-slot colour of the item inventory and storage grids

    /// <summary>Finds the item grid of the Storage Keeper window (its left half), by the 205 px red bar above it.</summary>
    public static InvLayout? LocateStorage(Frame f)
    {
        for (int y = 20; y < f.H - 220; y++)
        {
            int run = 0;
            for (int x = 0; x <= f.W; x++)
            {
                bool red = false;
                if (x < f.W) { int p = f.At(x, y); red = R(p) > 185 && G(p) < 70 && B(p) < 80; }
                if (red) { run++; continue; }
                if (run >= 204 && run <= 206 && GridOk(f, x - run + StorageDx, y + StorageDy)) return Snap(f, x - run + StorageDx, y + StorageDy);
                run = 0;
            }
        }
        return null;
    }

    /// <summary>
    /// The storage grid measured from its red bar can sit a pixel or two off the real slots. The icons still match
    /// there, but the counts are drawn in fixed rows of each slot and are missed. Moves the grid to where the most slot
    /// edges line up, within two pixels; with none lining up anywhere, it stays where the bar put it.
    /// </summary>
    static InvLayout Snap(Frame f, int gx, int gy)
    {
        int bestX = gx, bestY = gy, best = Aligned(f, gx, gy);
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (Aligned(f, gx + dx, gy + dy) is var a && a > best) { best = a; bestX = gx + dx; bestY = gy + dy; }
        return new InvLayout(bestX, bestY, -1);
    }

    /// <summary>True when the scrollbar right of the grid has a (blue) thumb, i.e. there are more rows than the ones showing.</summary>
    public static bool Scrollable(Frame f, InvLayout lay)
    {
        int n = 0;
        for (int y = lay.GridY + 14; y < lay.GridY + Rows * PitchY - 16; y++)
            for (int x = lay.GridX + Cols * PitchX; x < Math.Min(f.W, lay.GridX + Cols * PitchX + 18); x++)
            {
                int p = f.At(x, y);
                if (B(p) > R(p) + 50 && B(p) > 150) n++;
            }
        return n >= 20;
    }

    static bool GridOk(Frame f, int gx, int gy)
    {
        if (gx < 0 || gx + Cols * PitchX > f.W || gy + Rows * PitchY > f.H) return false;
        var counts = new Dictionary<int, int>();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                int p = f.At(gx + c * PitchX + Slot - 2, gy + r * PitchY + 1) & 0xFFFFFF;
                counts[p] = counts.GetValueOrDefault(p) + 1;
            }
        var top = counts.MaxBy(kv => kv.Value);
        return top.Value >= 12 && Near(top.Key, SlotColour, 16);
    }

    const int SlotEdge = 0xCEC9BE, SlotGap = 0xF5F6F7;   // the line down the left and along the top of a slot, and the panel between slots

    /// <summary>
    /// How many of the 30 slots start exactly at this grid position: each slot has a darker line on its left and
    /// top edge with the pale panel just outside it. A grid placed even one pixel off scores close to zero.
    /// </summary>
    public static int Aligned(Frame f, int gx, int gy)
    {
        if (gx < 1 || gy < 2 || gx + Cols * PitchX > f.W || gy + Rows * PitchY > f.H) return 0;
        int n = 0;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                int sx = gx + c * PitchX, sy = gy + r * PitchY;
                bool left = Near(f.At(sx, sy + 1) & 0xFFFFFF, SlotEdge, 12) && Near(f.At(sx - 1, sy + 1) & 0xFFFFFF, SlotGap, 12);
                bool top = Near(f.At(sx + 10, sy - 1) & 0xFFFFFF, SlotEdge, 12) && Near(f.At(sx + 10, sy - 2) & 0xFFFFFF, SlotGap, 12);
                if (left && top) n++;
            }
        return n;
    }

    /// <summary>True when a saved 32x32 slot picture was cut from a grid that was out of place: the gap between slots shows inside it.</summary>
    public static bool Offset(int[] px)
    {
        if (px.Length < Slot * Slot) return false;
        for (int x = 0; x < Slot - 1; x++)
        {
            int gap = 0;
            for (int y = 0; y < 6; y++) if (Near(px[y * Slot + x] & 0xFFFFFF, SlotGap, 6)) gap++;
            if (gap >= 5) return true;
        }
        for (int y = 2; y < Slot - 1; y++)
        {
            int gap = 0;
            for (int x = 0; x < 6; x++) if (Near(px[y * Slot + x] & 0xFFFFFF, SlotGap, 6)) gap++;
            if (gap >= 5) return true;
        }
        return false;
    }

    /// <summary>True when a saved slot picture is just an empty slot, or is not a slot at all.</summary>
    public static bool Blank(int[] px)
    {
        if (px.Length < Slot * Slot) return false;
        int other = 0, slot = 0;
        foreach (int p in px) { if (Near(p & 0xFFFFFF, SlotColour, 10)) slot++; else if (!Near(p & 0xFFFFFF, SlotEdge, 10)) other++; }
        return other < 24 || slot < 30;   // nothing in it, or no slot showing around it at all (something was covering the grid)
    }

    static bool Confirm(Frame f, int barX, int barY, out InvLayout? lay)
    {
        lay = null;
        int gx = barX + 5, gy = barY + 8;
        if (gx + Cols * PitchX > f.W || gy + Rows * PitchY > f.H) return false;
        // The slot grid has a flat beige background: check most slot corners share one colour.
        var counts = new Dictionary<int, int>();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                int p = f.At(gx + c * PitchX + Slot - 2, gy + r * PitchY + 1) & 0xFFFFFF;
                counts[p] = counts.GetValueOrDefault(p) + 1;
            }
        var top = counts.MaxBy(kv => kv.Value);
        if (top.Value < 12 || !Near(top.Key, SlotColour, 16)) return false;   // not the Equipment window, whose squares are white
        // The red bar can be partly covered (another window, a tooltip), which puts the grid a few pixels out.
        // Move it onto the slot edges; if the slots cannot be lined up, this is not a grid to read.
        if (Aligned(f, gx, gy) < 18)
        {
            int best0 = 0, bx = 0, by = 0;
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -12; dx <= 12; dx++)
                {
                    int a = Aligned(f, gx + dx, gy + dy);
                    if (a > best0) { best0 = a; bx = dx; by = dy; }
                }
            if (best0 < 18) return false;
            gx += bx; gy += by; barX += bx; barY += by;
        }
        // Active tab: the pink one.
        int best = -1, bestCount = 0;
        for (int t = 0; t < 6; t++)
        {
            int n = 0;
            for (int y = barY - 16; y < barY - 1; y++)
                for (int x = barX + 2 + t * 33; x < barX + 2 + t * 33 + 32; x++)
                {
                    if (x < 0 || y < 0 || x >= f.W) continue;
                    int p = f.At(x, y);
                    if (R(p) > 200 && G(p) < 190 && B(p) > 80 && B(p) < 215 && R(p) - G(p) > 55 && R(p) - B(p) > 35) n++;
                }
            if (n > bestCount) { bestCount = n; best = t; }
        }
        lay = new InvLayout(gx, gy, bestCount > 30 ? best : -1);
        return true;
    }

    public static int SlotBackground(Frame f, InvLayout lay)
    {
        var counts = new Dictionary<int, int>();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                for (int y = 0; y < 14; y++)
                    for (int x = 4; x < Slot; x++)
                    {
                        int p = f.At(lay.GridX + c * PitchX + x, lay.GridY + r * PitchY + y) | unchecked((int)0xFF000000);
                        counts[p] = counts.GetValueOrDefault(p) + 1;
                    }
        return counts.MaxBy(kv => kv.Value).Key;
    }

    public List<SlotRead> ReadSlots(Frame f, InvLayout lay)
    {
        int bg = SlotBackground(f, lay);
        var result = new List<SlotRead>();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                var s = new SlotRead { Col = c, Row = r, Pixels = new int[Slot * Slot] };
                int fg = 0;
                for (int y = 0; y < Slot; y++)
                    for (int x = 0; x < Slot; x++)
                    {
                        int p = f.At(lay.GridX + c * PitchX + x, lay.GridY + r * PitchY + y) | unchecked((int)0xFF000000);
                        s.Pixels[y * Slot + x] = p;
                        if (x >= 3 && x < Slot - 3 && y >= 3 && y < Slot - 3 && !Near(p, bg, 14)) fg++;
                    }
                if (fg < 25) continue; // empty slot
                result.Add(s);
            }
        Parallel.ForEach(result, s =>
        {
            Match(s, bg);
            (s.Quantity, s.QuantityKnown, s.HasCount) = Digits.Read(s.Pixels);
        });
        return result;
    }

    /// <summary>Matches the 32x32 square at (x, y) of a frame against the item icons.</summary>
    public (int id, double score) MatchAt(Frame f, int x, int y, int bg, int onlyId = 0)
    {
        if (x < 0 || y < 0 || x + Slot > f.W || y + Slot > f.H) return (0, 0);
        var s = new SlotRead { Pixels = new int[Slot * Slot] };
        for (int yy = 0; yy < Slot; yy++)
            for (int xx = 0; xx < Slot; xx++)
                s.Pixels[yy * Slot + xx] = f.At(x + xx, y + yy) | unchecked((int)0xFF000000);
        Match(s, bg, onlyId);
        return (s.ItemId, s.Score);
    }

    const int Tol = 36;
    const int DigitZoneY = 19; // stack counts are drawn over the bottom rows of the icon

    /// <summary>With onlyId set, scores just that item's icon (for telling apart items that share a picture).</summary>
    void Match(SlotRead s, int bg, int onlyId = 0)
    {
        var px = s.Pixels;
        // Foreground mask of the upper part of the slot (not covered by the stack count).
        var fgTop = new bool[Slot * DigitZoneY];
        int fgTopCount = 0;
        for (int y = 2; y < DigitZoneY; y++)
            for (int x = 2; x < Slot - 2; x++)
                if (!Near(px[y * Slot + x], bg, 14)) { fgTop[y * Slot + x] = true; fgTopCount++; }

        double best = 0; int bestId = 0;
        var close = new List<(int id, double score, double diff)>();
        foreach (var t in _templates)
        {
            double tBest = 0, tDiff = 0;
            if (onlyId != 0 && t.Id != onlyId) continue;
            // Inventory icons can extend a few pixels beyond the slot crop, even when
            // the PNG is smaller than 32x32 (e.g. Meba and Blue Lappy Boots).
            // Requiring the entire template to fit turns those exact matches into unknowns.
            for (int dy = Math.Min(0, Slot - t.H) - 3; dy <= Math.Max(0, Slot - t.H) + 3; dy++)
                for (int dx = Math.Min(0, Slot - t.W) - 3; dx <= Math.Max(0, Slot - t.W) + 3; dx++)
                {
                    int topN = 0, topOk = 0, botN = 0, botOk = 0, covered = 0; long diff = 0;
                    for (int ty = 0; ty < t.H; ty++)
                    {
                        int sy = ty + dy;
                        if (sy < 0 || sy >= Slot) continue;
                        for (int tx = 0; tx < t.W; tx++)
                        {
                            int sx = tx + dx;
                            if (sx < 0 || sx >= Slot) continue;
                            int tp = t.Px[ty * t.W + tx];
                            if ((tp >>> 24) < 250) continue;
                            bool ok = Near(tp, px[sy * Slot + sx], Tol);
                            if (sy < DigitZoneY)
                            {
                                topN++; if (ok) topOk++;
                                int sp = px[sy * Slot + sx];
                                diff += Math.Abs(((tp >> 16) & 255) - ((sp >> 16) & 255)) + Math.Abs(((tp >> 8) & 255) - ((sp >> 8) & 255)) + Math.Abs((tp & 255) - (sp & 255));
                                if (fgTop[sy * Slot + sx]) covered++;
                            }
                            else { botN++; if (ok) botOk++; }
                        }
                        // early exit: top rows already too poor to beat the current best
                        if (sy == DigitZoneY - 1 && topN > 30 && (double)topOk / topN < 0.5) break;
                    }
                    if (topN < 25) continue;
                    double top = (double)topOk / topN;
                    double bot = botN > 0 ? (double)botOk / botN : top;
                    double extra = fgTopCount > 0 ? (double)(fgTopCount - covered) / fgTopCount : 0;
                    double score = top * 0.75 + bot * 0.25 - Math.Max(0, extra - 0.08) * 0.6;
                    double avg = (double)diff / topN;
                    if (score > tBest + 0.005 || (score > tBest - 0.005 && avg < tDiff)) { tBest = Math.Max(tBest, score); tDiff = avg; }
                    if (score > best) { best = score; bestId = t.Id; }
                }
            if (tBest >= 0.72) close.Add((t.Id, tBest, tDiff));
        }
        // Items that differ only in colour (the ores) all pass the per-pixel tolerance: the closest colours decide.
        var rivals = close.Where(c => c.score >= best - 0.03).ToList();
        if (rivals.Count > 1) bestId = rivals.OrderBy(c => c.diff).First().id;
        s.Score = best;
        s.ItemId = best >= 0.72 ? bestId : 0;
    }

    /// <summary>Text dump of a slot's lower-left corner, used to build the digit glyphs.</summary>
    public static string DumpSlot(int[] px)
    {
        var sb = new StringBuilder();
        for (int y = 17; y < Slot; y++)
        {
            for (int x = 0; x < Slot; x++) sb.Append(Digits.Classify(px[y * Slot + x]));
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>Reads the stack count drawn over the lower-left corner of an inventory slot.</summary>
public static class Digits
{
    static int R(int p) => (p >> 16) & 255;
    static int G(int p) => (p >> 8) & 255;
    static int B(int p) => p & 255;

    /// <summary>'#' outline (black), 'o' digit stroke (white to light blue), '.' anything else.</summary>
    public static char Classify(int p)
    {
        int r = R(p), g = G(p), b = B(p);
        if (r < 40 && g < 40 && b < 40) return '#';
        if (b >= 235 && g >= 200 && r >= 150 && g >= r && b >= g) return 'o';
        return '.';
    }

    const int Top = 20, GlyphH = 9; // digit strokes occupy slot rows 20..28

    // Stroke patterns measured from the game. The 4 is inferred from the font's style (no sample seen yet).
    static readonly (int digit, string[] rows)[] Glyphs =
    {
        (0, new[] { ".oooo.", "o....o", "o....o", "o....o", "o....o", "o....o", "o....o", "o....o", ".oooo." }),
        (1, new[] { "..o", "ooo", "..o", "..o", "..o", "..o", "..o", "..o", "..o" }),
        (2, new[] { ".oooo.", "o....o", ".....o", ".....o", "....o.", "..oo..", ".o....", "o.....", "oooooo" }),
        (3, new[] { ".oooo.", "o....o", ".....o", ".....o", "..ooo.", ".....o", ".....o", "o....o", ".oooo." }),
        (4, new[] { "....o.", "...oo.", "..o.o.", ".o..o.", "o...o.", "oooooo", "....o.", "....o.", "....o." }),
        (5, new[] { "oooooo", "o.....", "o.....", "ooooo.", ".....o", ".....o", ".....o", "o....o", ".oooo." }),
        (6, new[] { "..ooo.", ".o....", "o.....", "ooooo.", "o....o", "o....o", "o....o", "o....o", ".oooo." }),
        (7, new[] { "oooooo", ".....o", "....o.", "....o.", "...o..", "...o..", "..o...", "..o...", ".o...." }),
        (8, new[] { ".oooo.", "o....o", "o....o", "o....o", ".oooo.", "o....o", "o....o", "o....o", ".oooo." }),
        (9, new[] { ".oooo.", "o....o", "o....o", "o....o", "o....o", ".ooooo", ".....o", ".....o", ".oooo." }),
    };

    static (int digit, int width, double score) BestAt(bool[] fill, int x0)
    {
        int bestDigit = -1, bestW = 0; double best = 0;
        foreach (var (digit, rows) in Glyphs)
        {
            int w = rows[0].Length;
            if (x0 + w > Vision.Slot) continue;
            int want = 0, hit = 0, extra = 0;
            for (int y = 0; y < GlyphH; y++)
                for (int x = 0; x < w; x++)
                {
                    bool f = fill[(Top + y) * Vision.Slot + x0 + x];
                    if (rows[y][x] == 'o') { want++; if (f) hit++; }
                    else if (f) extra++;
                }
            double score = (double)hit / want - 0.25 * extra / want;
            if (score > best) { best = score; bestDigit = digit; bestW = w; }
        }
        return (bestDigit, bestW, best);
    }

    public static (int qty, bool known, bool drawn) Read(int[] px)
    {
        var fill = new bool[px.Length];
        for (int i = 0; i < px.Length; i++) fill[i] = Classify(px[i]) == 'o';

        // First digit starts within the first few columns.
        int x = -1; (int digit, int width, double score) first = (-1, 0, 0);
        for (int x0 = 0; x0 <= 3; x0++)
        {
            var g = BestAt(fill, x0);
            if (g.score > first.score) { first = g; x = x0; }
        }
        if (first.score < 0.8) return (1, first.score < 0.5, false); // no count drawn (equips), or unreadable

        int value = 0, n = 0;
        var cur = first;
        while (cur.score >= 0.8 && n < 4)
        {
            value = value * 10 + cur.digit;
            n++;
            x += cur.width + 2;
            if (x + 3 > Vision.Slot) break;
            cur = BestAt(fill, x);
        }
        return (Math.Max(value, 1), true, true);
    }
}

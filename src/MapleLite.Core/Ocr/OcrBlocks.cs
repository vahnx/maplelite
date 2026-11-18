using MapleLite.Core.Imaging;

namespace MapleLite.Core.Ocr;

/// <summary>A row of text read on its own by <see cref="OcrBlocks.ReadTextRowsAsync"/>; Box is in frame pixels.</summary>
public sealed record TextRow(string Text, Region Box);

/// <summary>
/// Reading techniques for hard game text that more than one plugin needs: dense blocks read row by row, words the
/// engine split apart, reads retried until they check out, and counting what is drawn so a read can be verified.
/// </summary>
public static class OcrBlocks
{
    /// <summary>
    /// Reads a block of text row by row. Read as one block, OCR engines drop whole lines in dense game text (stat
    /// lists, tooltips). Here the pixels <paramref name="isInk"/> accepts become black on white, columns inked nearly
    /// all the way down are cleared (a window or tooltip edge showing through would join every row into one), the
    /// block is cut into rows where the ink is, and every row of a text's height is read on its own, enlarged.
    /// </summary>
    public static async Task<IReadOnlyList<TextRow>> ReadTextRowsAsync(this IOcr ocr, Frame frame, Region region, Func<int, bool> isInk,
        int scale = 4, int pad = 8, int minRowHeight = 5, int maxRowHeight = 22, CancellationToken ct = default)
    {
        var r = region.Intersect(frame.Bounds);
        var rows = new List<TextRow>();
        if (r.Width < 8 || r.Height < 8) return rows;
        int w = r.Width, h = r.Height;
        var mask = new Frame(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                mask.Px[y * w + x] = isInk(frame.At(r.X + x, r.Y + y)) ? Pixel.Black : Pixel.White;
        for (int x = 0; x < w; x++)
        {
            int ink = 0;
            for (int y = 0; y < h; y++) if (mask.Px[y * w + x] == Pixel.Black) ink++;
            if (ink > h * 0.7) for (int y = 0; y < h; y++) mask.Px[y * w + x] = Pixel.White;
        }
        foreach (var (top, bottom) in InkBands(mask, 2, 2))
        {
            if (bottom - top < minRowHeight || bottom - top > maxRowHeight) continue;   // a line of text, not an icon or a speck
            int y0 = Math.Max(0, top - 3), h0 = Math.Min(h - y0, bottom - top + 7);
            var one = await ocr.TryReadAsync(mask, new OcrRequest(new Region(0, y0, w, h0)) { Scale = scale, Scaling = OcrScaling.Smooth, Pad = pad }, ct);
            var text = string.Join(" ", one.OrderBy(l => l.Box.Left).Select(l => l.Text)).Trim();
            if (text.Length > 0) rows.Add(new TextRow(text, new Region(r.X, r.Y + y0, w, h0)));
        }
        return rows;
    }

    /// <summary>Bands of rows holding at least <paramref name="minInk"/> black pixels, closed after <paramref name="gap"/> empty rows.</summary>
    public static List<(int top, int bottom)> InkBands(Frame mask, int minInk = 2, int gap = 2)
    {
        var bands = new List<(int, int)>();
        int start = -1, blank = 0;
        for (int y = 0; y <= mask.H; y++)
        {
            int ink = 0;
            if (y < mask.H) for (int x = 0; x < mask.W; x++) if (mask.Px[y * mask.W + x] == Pixel.Black) ink++;
            if (ink >= minInk) { if (start < 0) start = y; blank = 0; }
            else if (start >= 0 && (++blank >= gap || y == mask.H)) { bands.Add((start, y - blank)); start = -1; blank = 0; }
        }
        return bands;
    }

    /// <summary>
    /// The lines as read, plus lines made by joining neighbours on the same row: engines sometimes read one title as
    /// separate words ("Dark" and "Guise"). A join stops at a gap wider than <paramref name="maxGap"/>, so two windows
    /// or tooltips side by side stay apart. The original lines come first.
    /// </summary>
    public static List<OcrLine> WithJoinedNeighbours(IReadOnlyList<OcrLine> lines, double maxGap = 18, int maxJoin = 3)
    {
        var all = lines.ToList();
        foreach (var first in lines)
        {
            var words = first.Words.ToList();
            var last = first;
            for (int n = 0; n < maxJoin; n++)
            {
                var next = lines.Where(l => l != last && Math.Abs(l.Box.CenterY - last.Box.CenterY) < 5 && l.Box.Left > last.Box.Right - 2 && l.Box.Left - last.Box.Right < maxGap)
                    .OrderBy(l => l.Box.Left).FirstOrDefault();
                if (next == null) break;
                words.AddRange(next.Words);
                last = next;
                all.Add(OcrLine.FromWords(words));
            }
        }
        return all;
    }

    /// <summary>
    /// Tries reads one after another until <paramref name="accept"/> takes one (returns non-null). Pixel fonts lose a
    /// digit at one enlargement and not at another, so a value that must be exact (a price) is read several ways and
    /// kept only when it checks out, for example against <see cref="ImageOps.CountInkRuns"/>.
    /// </summary>
    public static async Task<(T? value, IReadOnlyList<OcrLine> lines)> ReadUntilAsync<T>(this IOcr ocr, Frame frame, IEnumerable<OcrRequest> tries,
        Func<IReadOnlyList<OcrLine>, T?> accept, CancellationToken ct = default) where T : class
    {
        IReadOnlyList<OcrLine> last = Array.Empty<OcrLine>();
        foreach (var request in tries)
        {
            last = await ocr.TryReadAsync(frame, request, ct);
            if (accept(last) is { } value) return (value, last);
        }
        return (null, last);
    }

    /// <summary>The usual ways to re-read a small line: 2x smooth first (best for the game's fonts), then nearest, bicubic, larger.</summary>
    public static IEnumerable<OcrRequest> Variants(IEnumerable<Region> regions, int pad = 4)
    {
        var ways = new[] { (2, OcrScaling.Smooth), (3, OcrScaling.Nearest), (4, OcrScaling.Nearest), (3, OcrScaling.Smooth), (2, OcrScaling.Bicubic), (3, OcrScaling.Bicubic) };
        var list = regions.ToList();
        foreach (var (scale, scaling) in ways)
            foreach (var r in list)
                yield return new OcrRequest(r) { Scale = scale, Scaling = scaling, Pad = pad };
    }
}

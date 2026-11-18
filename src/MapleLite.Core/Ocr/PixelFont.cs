using System.Text;
using MapleLite.Core.Imaging;

namespace MapleLite.Core.Ocr;

/// <summary>
/// Reads text drawn in a fixed pixel font (no smoothing, one size) by matching each glyph against known shapes.
/// Exact where general OCR struggles: the game's small UI numbers are a few pixels tall, and OCR engines misread
/// commas, brackets and percent signs in them. Glyphs are found as runs of columns holding ink, so they must not touch.
/// </summary>
public sealed class PixelFont
{
    readonly record struct Glyph(char Char, int Width, ulong[] Columns);

    readonly List<Glyph> _glyphs = new();

    /// <summary>Rows in a line of this font, from the top of the tallest glyph to the baseline.</summary>
    public int Height { get; }

    /// <summary>A gap of at least this many empty columns is read as a space.</summary>
    public int SpaceGap { get; init; } = 6;

    /// <param name="height">Rows per glyph.</param>
    /// <param name="glyphs">Each character with its rows, top to bottom, '#' for ink. Rows may be of any width; empty edge columns are trimmed.</param>
    public PixelFont(int height, IEnumerable<(char c, string[] rows)> glyphs)
    {
        if (height is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(height));
        Height = height;
        foreach (var (c, rows) in glyphs)
        {
            if (rows.Length != height) throw new ArgumentException($"'{c}' has {rows.Length} rows, not {height}.");
            int w = rows.Max(r => r.Length);
            var cols = new ulong[w];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < height; y++)
                    if (x < rows[y].Length && rows[y][x] == '#') cols[x] |= 1UL << y;
            int a = 0, b = w - 1;
            while (a <= b && cols[a] == 0) a++;
            while (b >= a && cols[b] == 0) b--;
            if (a > b) throw new ArgumentException($"'{c}' has no ink.");
            _glyphs.Add(new Glyph(c, b - a + 1, cols[a..(b + 1)]));
        }
    }

    /// <summary>
    /// Reads one line whose glyph tops sit on row <paramref name="top"/>, between columns <paramref name="x0"/> and
    /// <paramref name="x1"/> (exclusive). Unknown shapes come back as '?'. A shape off by one pixel from exactly one
    /// glyph is accepted as that glyph.
    /// </summary>
    public string ReadLine(Frame f, int top, int x0, int x1, Func<int, bool> isInk)
    {
        x0 = Math.Max(0, x0); x1 = Math.Min(f.W, x1);
        if (top < 0 || top + Height > f.H || x1 <= x0) return "";
        int n = x1 - x0;
        var cols = new ulong[n];
        for (int y = 0; y < Height; y++)
        {
            int row = (top + y) * f.W;
            for (int i = 0; i < n; i++)
                if (isInk(f.Px[row + x0 + i])) cols[i] |= 1UL << y;
        }
        var sb = new StringBuilder();
        int x = 0, gap = 0;
        bool any = false;
        while (x < n)
        {
            if (cols[x] == 0) { x++; gap++; continue; }
            int start = x;
            while (x < n && cols[x] != 0) x++;
            if (any && gap >= SpaceGap) sb.Append(' ');
            sb.Append(Match(cols.AsSpan(start, x - start)));
            any = true; gap = 0;
        }
        return sb.ToString();
    }

    char Match(ReadOnlySpan<ulong> cols)
    {
        char best = '?';
        int bestDiff = int.MaxValue, ties = 0;
        foreach (var g in _glyphs)
        {
            if (g.Width != cols.Length) continue;
            int diff = 0;
            for (int i = 0; i < cols.Length; i++) diff += System.Numerics.BitOperations.PopCount(g.Columns[i] ^ cols[i]);
            if (diff == 0) return g.Char;
            if (diff < bestDiff) { bestDiff = diff; best = g.Char; ties = 1; }
            else if (diff == bestDiff) ties++;
        }
        return bestDiff == 1 && ties == 1 ? best : '?';
    }
}

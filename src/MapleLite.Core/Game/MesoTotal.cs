using System.Globalization;
using System.Text.RegularExpressions;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>
/// Reads the meso total at the bottom of the inventory window: black digits with thousands separators, right-aligned
/// in a white box beside the "MESOS" label. The window can sit anywhere, so <see cref="LocateAsync"/> looks over the
/// whole picture once; after that <see cref="ReadAsync"/> reads just that box, which is quick.
/// </summary>
public static partial class MesoTotal
{
    [GeneratedRegex(@"^\d{1,3}(?:[,.]\d{3})+$|^\d{1,3}$")]
    private static partial Regex Amount();

    static readonly OcrPreprocessor Digits = OcrPreprocessor.DarkText(100);

    /// <summary>Parses "1,837,311" (or "1.837.311"); null for anything else.</summary>
    public static long? Parse(string text)
    {
        text = text.Trim();
        if (!Amount().IsMatch(text)) return null;
        return long.TryParse(text.Replace(",", "").Replace(".", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>
    /// Finds the meso total in the picture: a number written in a white field (the box is white well to the left of
    /// the right-aligned digits), which rules out damage numbers, levels and other figures on the scene.
    /// Returns the area to read from now on (wide enough for the total to grow leftwards) and the value read.
    /// </summary>
    public static async Task<(Region Box, long Value)?> LocateAsync(IOcr ocr, Frame f, CancellationToken ct = default)
    {
        var r = await ocr.ReadAsync(f, new OcrRequest(f.Bounds) { Preprocess = Digits, Scale = 3, Scaling = OcrScaling.Bicubic, Cache = false }, ct).ConfigureAwait(false);
        foreach (var w in r.Lines.SelectMany(l => l.Words))
        {
            if (Parse(w.Text) is not { } value || !w.Text.Contains(',') && !w.Text.Contains('.')) continue;
            int y = (int)w.Box.CenterY, x1 = (int)w.Box.Left - 4, x0 = x1 - 40;
            if (x0 < 0 || y <= 0 || y >= f.H || !MostlyWhite(f, x0, x1, y)) continue;
            int top = Math.Max(0, (int)w.Box.Top - 3), bottom = Math.Min(f.H, (int)Math.Ceiling(w.Box.Bottom) + 3);
            int right = Math.Min(f.W, (int)Math.Ceiling(w.Box.Right) + 4), left = Math.Max(0, right - 110);
            return (new Region(left, top, right - left, bottom - top), value);
        }
        return null;
    }

    /// <summary>The total in an area found by <see cref="LocateAsync"/>; null when it isn't there (window closed or moved).</summary>
    public static async Task<long?> ReadAsync(IOcr ocr, Frame f, Region box, CancellationToken ct = default)
    {
        if (box.Right > f.W || box.Bottom > f.H) return null;
        if (!MostlyWhite(f, box.X, box.X + 30, box.Y + box.Height / 2)) return null; // the white box isn't there any more
        var r = await ocr.ReadAsync(f, new OcrRequest(box) { Preprocess = Digits, Scale = 3, Pad = 6, Scaling = OcrScaling.Bicubic, Cache = false }, ct).ConfigureAwait(false);
        var text = string.Concat(r.Lines.Select(l => l.Text)).Replace(" ", "");
        return Parse(text);
    }

    static bool MostlyWhite(Frame f, int x0, int x1, int y)
    {
        int white = 0, n = 0;
        for (int x = Math.Max(0, x0); x < Math.Min(f.W, x1); x++, n++)
        {
            int p = f.Px[y * f.W + x];
            if (Math.Min((p >> 16) & 255, Math.Min((p >> 8) & 255, p & 255)) >= 225) white++;
        }
        return n > 0 && white >= n * 0.9;
    }
}

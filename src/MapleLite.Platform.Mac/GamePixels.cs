using MapleLite.Core.Imaging;

namespace MapleLite.Platform.Mac;

/// <summary>
/// Brings Retina captures back to the game's own pixels.
///
/// The game draws at its own resolution (1366x768 and so on) and macOS shows each of its pixels as a 2x2 block on a
/// Retina display, so ScreenCaptureKit hands back a picture twice as wide and tall. Every reader (chat, mesos,
/// minimap, name tags, tooltips) measures in game pixels, as the Windows capture delivers them, so the Mac capture
/// shrinks the picture by the display's backing scale before anyone sees it.
/// </summary>
internal static class GamePixels
{
    /// <summary>The whole-number backing scale to undo (2 on Retina), or 1 when there is nothing to undo.</summary>
    public static int Factor(double scale)
    {
        int s = (int)Math.Round(scale);
        return s >= 2 && Math.Abs(scale - s) < 0.01 ? s : 1;
    }

    /// <summary>
    /// The title bar height to cut, moved down onto a block boundary. A title bar is a whole number of points, so
    /// its height in pixels is a multiple of the scale. The detector counts the 1px dark line macOS draws along the
    /// top of the content as part of the bar (57 instead of 56 on Retina), but that line lies over the game's first
    /// row, so the game starts at the multiple below. Cutting a row lower would drop the game's top row and shift
    /// every reader's fixed positions up a pixel (the minimap check, for one, then misses).
    /// </summary>
    public static int AlignTop(int top, int s) => s <= 1 ? top : top - top % s;

    /// <summary>
    /// Averages each s x s block into one pixel. Where macOS doubled the game's pixels (the normal case) the block
    /// is uniform and this gives back the game's exact pixel; elsewhere it is a fair box filter.
    /// </summary>
    public static Frame Shrink(Frame src, int s)
    {
        if (s <= 1) return src;
        int w = src.W / s, h = src.H / s;
        if (w < 1 || h < 1) return src;
        var px = new int[w * h];
        int n = s * s, half = n / 2;
        var sp = src.Px;
        for (int y = 0; y < h; y++)
        {
            int row = y * s * src.W;
            for (int x = 0; x < w; x++)
            {
                int i0 = row + x * s;
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < s; dy++)
                {
                    int i = i0 + dy * src.W;
                    for (int dx = 0; dx < s; dx++)
                    {
                        int p = sp[i + dx];
                        r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255;
                    }
                }
                px[y * w + x] = unchecked((int)0xff000000) | ((r + half) / n << 16) | ((g + half) / n << 8) | ((b + half) / n);
            }
        }
        return new Frame(w, h, px);
    }
}

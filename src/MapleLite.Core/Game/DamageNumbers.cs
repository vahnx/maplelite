using MapleLite.Core.Imaging;

namespace MapleLite.Core.Game;

/// <summary>
/// Finds the damage numbers that pop up over monsters when they're hit: big digits filled red → orange → yellow,
/// with a white outline inside a black one. That white ring is what tells them apart from orange mushrooms, apples
/// and other warm-coloured scenery. Each number (its digits, and a second hit stacked under it) comes back as one box.
/// </summary>
public static class DamageNumbers
{
    static bool Hot(int p)
    {
        int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
        return r >= 200 && b <= 90 && r - b >= 150 && g <= r;
    }

    static bool White(int p) => Pixel.Min(p) >= 225;

    /// <summary>Damage-number boxes in the picture, skipping the bottom <paramref name="ignoreBottom"/> rows (the status bar's gauges).</summary>
    public static List<Region> Find(Frame f, int ignoreBottom = 70)
    {
        int W = f.W, H = Math.Max(0, f.H - ignoreBottom);
        var seen = new bool[W * H];
        var digits = new List<Region>();
        var stack = new Stack<int>();
        var pix = new List<int>();
        for (int i = 0; i < W * H; i++)
        {
            if (seen[i] || !Hot(f.Px[i])) continue;
            seen[i] = true; stack.Push(i); pix.Clear();
            int minx = W, maxx = 0, miny = H, maxy = 0;
            while (stack.Count > 0)
            {
                int k = stack.Pop(); pix.Add(k);
                int x = k % W, y = k / W;
                if (x < minx) minx = x; if (x > maxx) maxx = x; if (y < miny) miny = y; if (y > maxy) maxy = y;
                if (pix.Count > 3000) continue; // too big for a digit; finish marking without growing the list much
                if (x > 0 && !seen[k - 1] && Hot(f.Px[k - 1])) { seen[k - 1] = true; stack.Push(k - 1); }
                if (x < W - 1 && !seen[k + 1] && Hot(f.Px[k + 1])) { seen[k + 1] = true; stack.Push(k + 1); }
                if (y > 0 && !seen[k - W] && Hot(f.Px[k - W])) { seen[k - W] = true; stack.Push(k - W); }
                if (y < H - 1 && !seen[k + W] && Hot(f.Px[k + W])) { seen[k + W] = true; stack.Push(k + W); }
            }
            int bw = maxx - minx + 1, bh = maxy - miny + 1;
            if (pix.Count < 120 || pix.Count > 3000 || bh < 16 || bh > 60 || bw > 60) continue;
            if (WhiteRing(f, pix, H) >= 0.6) digits.Add(new Region(minx, miny, bw, bh));
        }
        return Group(digits);
    }

    // Share of the shape's edge pixels with white within 3 px outward.
    static double WhiteRing(Frame f, List<int> pix, int H)
    {
        int W = f.W, edge = 0, ringed = 0;
        foreach (var k in pix)
        {
            int x = k % W, y = k / W;
            bool isEdge = false, touch = false;
            for (int d = 0; d < 4; d++)
            {
                int dx = d == 0 ? -1 : d == 1 ? 1 : 0, dy = d == 2 ? -1 : d == 3 ? 1 : 0;
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= W || yy >= H || Hot(f.Px[yy * W + xx])) continue;
                isEdge = true;
                for (int s = 1; s <= 3 && !touch; s++)
                {
                    int x3 = x + dx * s, y3 = y + dy * s;
                    if (x3 < 0 || y3 < 0 || x3 >= W || y3 >= H) break;
                    touch = White(f.Px[y3 * W + x3]);
                }
            }
            if (isEdge) { edge++; if (touch) ringed++; }
        }
        return edge == 0 ? 0 : (double)ringed / edge;
    }

    // Digits a few pixels apart (side by side, or a second hit stacked below) belong to one number.
    static List<Region> Group(List<Region> digits)
    {
        var groups = new List<Region>();
        foreach (var d in digits.OrderBy(d => d.Y))
        {
            int i = groups.FindIndex(g => d.X <= g.X + g.Width + 12 && d.X + d.Width >= g.X - 12 && d.Y <= g.Y + g.Height + 12 && d.Y + d.Height >= g.Y - 12);
            if (i < 0) { groups.Add(d); continue; }
            var g = groups[i];
            int x0 = Math.Min(g.X, d.X), y0 = Math.Min(g.Y, d.Y), x1 = Math.Max(g.X + g.Width, d.X + d.Width), y1 = Math.Max(g.Y + g.Height, d.Y + d.Height);
            groups[i] = new Region(x0, y0, x1 - x0, y1 - y0);
        }
        return groups;
    }
}

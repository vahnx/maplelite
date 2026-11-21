using MapleLite.Core.Game;

namespace MapleLite.Plugins.SaleCard;

/// <summary>
/// Builds a picture of your character out of many cut-outs of the game world. Wherever you stand the character
/// looks the same and the scenery behind it does not, so a pixel that keeps its colour across cut-outs taken in
/// front of different scenery belongs to the character, and the rest is made see-through.
/// </summary>
public sealed class PortraitBuilder
{
    const int W = AvatarReader.W, H = AvatarReader.H, Keep = 14;
    readonly List<int[]> _crops = new();   // lined up with the first one
    int _rejects;

    public int Count => _crops.Count;
    public void Clear() { _crops.Clear(); _rejects = 0; }

    static bool Close(int a, int b, int tol) =>
        Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= tol && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= tol && Math.Abs((a & 255) - (b & 255)) <= tol;

    static int[] Shift(int[] px, int dx, int dy, bool mirror)
    {
        var o = new int[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int sx = mirror ? W - 1 - (x - dx) : x - dx, sy = y - dy;
                if (sx >= 0 && sx < W && sy >= 0 && sy < H) o[y * W + x] = px[sy * W + sx];
            }
        return o;
    }

    /// <summary>How alike two cut-outs are in the middle, where the character stands.</summary>
    static double Middle(int[] a, int[] b, int dx, int dy, bool mirror)
    {
        int same = 0, all = 0;
        for (int y = 14; y < H - 4; y += 2)
            for (int x = 18; x < W - 18; x += 2)
            {
                int sx = mirror ? W - 1 - (x - dx) : x - dx, sy = y - dy;
                if (sx < 0 || sx >= W || sy < 0 || sy >= H) continue;
                all++;
                if (Close(a[y * W + x], b[sy * W + sx], 20)) same++;
            }
        return all == 0 ? 0 : (double)same / all;
    }

    /// <summary>How alike two cut-outs are around the edge, where only scenery shows.</summary>
    static double Edge(int[] a, int[] b)
    {
        int same = 0, all = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (x >= 7 && x < W - 7 && y >= 7 && y < H - 7) continue;
                int p = a[y * W + x], q = b[y * W + x];
                if (p == 0 || q == 0) continue;
                all++;
                if (Close(p, q, 20)) same++;
            }
        return all == 0 ? 1 : (double)same / all;
    }

    /// <summary>Takes a cut-out in. True when it showed the character in front of scenery not seen before.</summary>
    public bool Add(int[] crop)
    {
        if (_crops.Count == 0) { _crops.Add(crop); return true; }
        // Line it up with the first one: the name tag is not always read at quite the same spot, and you may be
        // facing the other way.
        (double score, int dx, int dy, bool mirror) best = (0, 0, 0, false);
        foreach (bool mirror in new[] { false, true })
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    double s = Middle(_crops[0], crop, dx, dy, mirror);
                    if (s > best.score) best = (s, dx, dy, mirror);
                }
        if (best.score < 0.35)
        {
            // Another pose (jumping, attacking, on a rope), or the first cut-out was the odd one: start over if it
            // keeps happening.
            if (++_rejects >= 8) { Clear(); _crops.Add(crop); return true; }
            return false;
        }
        _rejects = 0;
        var lined = Shift(crop, best.dx, best.dy, best.mirror);
        if (_crops.Any(c => Edge(lined, c) > 0.5)) return false;   // same spot as before: nothing new to learn
        _crops.Add(lined);
        if (_crops.Count > Keep) _crops.RemoveAt(1);
        return true;
    }

    /// <summary>The character alone, or null while too few different backdrops have been seen.</summary>
    public int[]? Build()
    {
        int n = _crops.Count;
        if (n < 4) return null;
        int need = (int)Math.Ceiling(n * 0.6);
        var o = new int[W * H];
        for (int i = 0; i < W * H; i++)
        {
            int bestAt = -1, bestN = 0;
            for (int a = 0; a < n; a++)
            {
                int p = _crops[a][i];
                if (p == 0) continue;
                int votes = 0;
                for (int b = 0; b < n; b++) if (_crops[b][i] != 0 && Close(p, _crops[b][i], 26)) votes++;
                if (votes > bestN) { bestN = votes; bestAt = a; }
            }
            if (bestN >= need) o[i] = _crops[bestAt][i];
        }
        // Plain scenery (sky, a wall) looks the same everywhere and survives the vote: clear what reaches in from
        // the top and sides through even colour. Not from the bottom, where the feet are.
        var seen = new bool[W * H];
        var queue = new Queue<int>();
        void Seed(int i) { if (!seen[i]) { seen[i] = true; queue.Enqueue(i); } }
        for (int x = 0; x < W; x++) Seed(x);
        for (int y = 0; y < H; y++) { Seed(y * W); Seed(y * W + W - 1); }
        var src = (int[])o.Clone();
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % W, y = i / W;
            o[i] = 0;
            void Try(int j) { if (!seen[j] && (src[j] == 0 || (src[i] != 0 && Close(src[i], src[j], 14)))) Seed(j); }
            if (x > 0) Try(i - 1);
            if (x < W - 1) Try(i + 1);
            if (y > 0) Try(i - W);
            if (y < H - 1) Try(i + W);
        }
        // Drop loose specks: keep the biggest piece and anything of real size.
        var label = new int[W * H];
        var sizes = new List<int> { 0 };
        for (int i = 0; i < W * H; i++)
        {
            if (o[i] == 0 || label[i] != 0) continue;
            int id = sizes.Count, size = 0;
            var stack = new Stack<int>();
            stack.Push(i); label[i] = id;
            while (stack.Count > 0)
            {
                int k = stack.Pop(), x = k % W, y = k / W;
                size++;
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(H - 1, y + 1); yy++)
                    for (int xx = Math.Max(0, x - 1); xx <= Math.Min(W - 1, x + 1); xx++)
                    {
                        int j = yy * W + xx;
                        if (o[j] != 0 && label[j] == 0) { label[j] = id; stack.Push(j); }
                    }
            }
            sizes.Add(size);
        }
        int biggest = sizes.Max();
        if (biggest < 300) return null;   // no character came out of it
        for (int i = 0; i < W * H; i++)
            if (o[i] != 0 && sizes[label[i]] != biggest && sizes[label[i]] < 40) o[i] = 0;
        return AvatarReader.Tidy(o);
    }
}

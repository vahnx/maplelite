using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>
/// Finds a picture of your character: the game writes your name under it, both in the world and in the
/// portrait of a shop window, so the picture is the patch just above wherever the IGN is written.
/// </summary>
public static class AvatarReader
{
    public const int W = 68, H = 84;

    /// <summary>
    /// Conservative cleanup for a manual snip: one fixed corner colour is backdrop. It never walks across a gradual
    /// colour ramp into the character and never deletes pieces.
    /// </summary>
    public static int[] ClearSnipBackground(int[] px)
    {
        var result = (int[])px.Clone();
        int background = px[0];
        bool Matches(int p) => Enumerable.Range(0, 3).All(c => Math.Abs(((p >> (c * 8)) & 255) - ((background >> (c * 8)) & 255)) <= 8);
        if (!Matches(px[W - 1]) || !Matches(px[(H - 1) * W]) || !Matches(px[^1])) return result;
        var seen = new bool[px.Length];
        var queue = new Queue<int>();
        void Visit(int i) { if (!seen[i] && Matches(px[i])) { seen[i] = true; queue.Enqueue(i); } }
        for (int x = 0; x < W; x++) { Visit(x); Visit((H - 1) * W + x); }
        for (int y = 0; y < H; y++) { Visit(y * W); Visit(y * W + W - 1); }
        while (queue.TryDequeue(out int i))
        {
            result[i] = 0;
            if (i % W > 0) Visit(i - 1);
            if (i % W < W - 1) Visit(i + 1);
            if (i >= W) Visit(i - W);
            if (i < W * (H - 1)) Visit(i + W);
        }
        return result;
    }

    public static int[] Crop(Frame f, int x0, int y0)
    {
        var px = new int[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                px[y * W + x] = f.At(x0 + x, y0 + y) | unchecked((int)0xFF000000);
        return px;
    }

    /// <summary>
    /// Makes a portrait's backdrop see-through: everything reachable from the picture's edge through smoothly
    /// changing colour is backdrop. The character's dark outline stops the spread.
    /// </summary>
    public static int[] ClearBackground(int[] px)
    {
        // Already-cut-out pixels are final. Transparent black must never become
        // a new flood-fill seed: it matches the character's black outlines.
        if (px.Any(p => (uint)p >> 24 != 255)) return (int[])px.Clone();
        var o = (int[])px.Clone();
        var seen = new bool[W * H];
        var queue = new Queue<int>();
        void Seed(int i) { if (!seen[i]) { seen[i] = true; queue.Enqueue(i); } }
        for (int x = 0; x < W; x++) { Seed(x); Seed((H - 1) * W + x); }
        for (int y = 0; y < H; y++) { Seed(y * W); Seed(y * W + W - 1); }
        static bool Close(int a, int b) => Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= 14 && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= 14 && Math.Abs((a & 255) - (b & 255)) <= 14;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % W, y = i / W;
            o[i] = 0;
            if (x > 0 && !seen[i - 1] && Close(px[i], px[i - 1])) Seed(i - 1);
            if (x < W - 1 && !seen[i + 1] && Close(px[i], px[i + 1])) Seed(i + 1);
            if (y > 0 && !seen[i - W] && Close(px[i], px[i - W])) Seed(i - W);
            if (y < H - 1 && !seen[i + W] && Close(px[i], px[i + W])) Seed(i + W);
        }
        return o;
    }

    /// <summary>
    /// Removes what is left around a cut-out character: specks, and pieces of the window it was taken from. The
    /// character is the biggest piece; anything touching or right beside it stays, and so does a sizeable piece
    /// directly above or below it (a halo, an effect). Everything else goes.
    /// </summary>
    public static int[] Tidy(int[] px)
    {
        var o = (int[])px.Clone();
        var label = new int[W * H];
        var sizes = new List<int> { 0 };
        var minX = new List<int> { 0 }; var maxX = new List<int> { 0 };
        for (int i = 0; i < W * H; i++)
        {
            if (((o[i] >> 24) & 255) == 0 || label[i] != 0) continue;
            int id = sizes.Count, size = 0, lo = W, hi = 0;
            var stack = new Stack<int>();
            stack.Push(i); label[i] = id;
            while (stack.Count > 0)
            {
                int k = stack.Pop(), x = k % W, y = k / W;
                size++; lo = Math.Min(lo, x); hi = Math.Max(hi, x);
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(H - 1, y + 1); yy++)
                    for (int xx = Math.Max(0, x - 1); xx <= Math.Min(W - 1, x + 1); xx++)
                    {
                        int j = yy * W + xx;
                        if (((o[j] >> 24) & 255) != 0 && label[j] == 0) { label[j] = id; stack.Push(j); }
                    }
            }
            sizes.Add(size); minX.Add(lo); maxX.Add(hi);
        }
        if (sizes.Count <= 2) return o;
        int main = sizes.IndexOf(sizes.Max());
        var keep = new bool[sizes.Count];
        keep[main] = true;
        for (int id = 1; id < sizes.Count; id++)
            if (id != main && sizes[id] >= 40 && minX[id] >= minX[main] - 1 && maxX[id] <= maxX[main] + 1) keep[id] = true;
        const int reach = 3;
        for (int i = 0; i < W * H; i++)
        {
            if (label[i] != main) continue;
            int x = i % W, y = i / W;
            for (int yy = Math.Max(0, y - reach); yy <= Math.Min(H - 1, y + reach); yy++)
                for (int xx = Math.Max(0, x - reach); xx <= Math.Min(W - 1, x + reach); xx++)
                    keep[label[yy * W + xx]] = true;
        }
        for (int i = 0; i < W * H; i++) if (label[i] != 0 && !keep[label[i]]) o[i] = 0;
        return o;
    }

    /// <summary>True when the crop's edge is mostly one plain colour: a portrait in a window, not the game world.</summary>
    static bool PlainBackdrop(int[] px)
    {
        var ring = new List<int>();
        for (int x = 0; x < W; x++) { ring.Add(px[x]); ring.Add(px[W + x]); ring.Add(px[(H - 2) * W + x]); ring.Add(px[(H - 1) * W + x]); }
        for (int y = 2; y < H - 2; y++) { ring.Add(px[y * W]); ring.Add(px[y * W + 1]); ring.Add(px[y * W + W - 2]); ring.Add(px[y * W + W - 1]); }
        int Mid(int shift) { var c = ring.Select(p => (p >> shift) & 255).OrderBy(c => c).ToList(); return c[c.Count / 2]; }
        int r = Mid(16), g = Mid(8), b = Mid(0);
        int near = ring.Count(p => Math.Abs(((p >> 16) & 255) - r) <= 22 && Math.Abs(((p >> 8) & 255) - g) <= 22 && Math.Abs((p & 255) - b) <= 22);
        return near >= ring.Count * 0.7;
    }

    /// <summary>
    /// Returns the picture and whether it is a clean portrait (Character Info or shop window) rather than a
    /// cut-out of the world. There are four ways of looking for the name; pass picks one of them (0 is the fastest),
    /// and -1 tries them all.
    /// </summary>
    public static async Task<(int[] px, bool clean)?> ReadAsync(IOcr ocr, Frame f, string ign, int pass = -1)
    {
        string Key(string t) => new string(t.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var want = Key(ign);
        if (want.Length < 3) return null;
        int bottom = f.H - 70; // leave out the status bar, which also shows the name
        if (bottom < H + 20) return null;

        // Name tags in the world are white on a dark strip, which the text reader misses among the scenery:
        // a second picture keeps only the white pixels, as black on white.
        Frame? tags = null;
        Frame Tags()
        {
            if (tags != null) return tags;
            tags = new Frame(f.W, bottom);
            for (int i = 0; i < tags.Px.Length; i++)
            {
                int p = f.Px[i], r = (p >> 16) & 255, g = (p >> 8) & 255, bl = p & 255;
                tags.Px[i] = r > 215 && g > 215 && bl > 215 ? unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF);
            }
            return tags;
        }
        int[]? world = null;
        var looks = new[] { (false, 1), (false, 2), (true, 2), (true, 3) };
        foreach (var (tagged, scale) in pass < 0 ? looks : new[] { looks[pass % looks.Length] })
        {
            var source = tagged ? Tags() : f;
            var lines = await ocr.TryReadAsync(source, new OcrRequest(new Region(0, 0, f.W, bottom)) { Scale = scale, Scaling = OcrScaling.Smooth });
            // Every place the name shows: its own line, or inside a line where tags of players standing side by side
            // were read as one ("Player1:Player2").
            foreach (var l in lines)
            {
                string k = Key(l.Text);
                int at = k.IndexOf(want, StringComparison.Ordinal);
                // A near match is unsafe here: it can select another player's name
                // and turn scenery into the user's portrait. Require the exact IGN.
                if (at < 0) continue;
                int cx = (int)(l.Left() + (l.Right() - l.Left()) * (at + want.Length / 2.0) / k.Length), y1 = (int)l.Cy() - 9;
                int x0 = cx - W / 2, y0 = y1 - H;
                if (x0 < 0 || y0 < 0 || x0 + W > f.W) continue;
                var crop = Crop(f, x0, y0);
                if (PlainBackdrop(crop)) return (crop, true);   // a portrait in a window: the best there is
                if (k.Length == want.Length || world == null) world = crop;
            }
        }
        return world != null ? (world, false) : null;
    }
}

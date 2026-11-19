using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Plugins.SpawnTimer;

/// <summary>
/// Finds your character on screen by its name tag. Your name comes from MapleLite's shared game state (read from the status bar);
/// the tag is then looked for in a read of the whole picture. Tags are often read glued to a neighbour's
/// ("SnailMaplePlayer"), so the name is searched for inside each line, allowing a slip or two.
/// </summary>
public sealed class PlayerFinder(IOcr ocr, Func<string?>? sharedName = null)
{
    public string? Name { get; private set; }

    /// <summary>Your name tag's centre in the picture, or null if it can't be seen.</summary>
    public async Task<(double X, double Y)?> FindAsync(Frame f, CancellationToken ct = default)
    {
        // MapleLite reads your name once for every plugin (context.Game.Ign); read it here only without that
        Name ??= sharedName?.Invoke() ?? await IgnReader.ReadAsync(ocr, f);
        if (Name == null) return null;
        // The camera keeps you near the middle, and tags are small: read the middle band at 2×. On a recorded session
        // this found the tag in 68% of pictures at ~150 ms, against 30% at ~800 ms for the whole picture at normal size.
        var band = new Region(f.W / 5, f.H / 4, f.W * 3 / 5, Math.Max(1, f.H * 3 / 4 - 70));
        var read = await ocr.TryReadAsync(f, new OcrRequest(band) { Scale = 2 }, ct);
        return Locate(read, Name);
    }

    public static (double X, double Y)? Locate(IEnumerable<OcrLine> lines, string name)
    {
        string want = Clean(name);
        (double X, double Y)? best = null;
        int bestErr = int.MaxValue;
        int limit = Math.Max(1, want.Length / 5);
        foreach (var l in lines)
        {
            // runs of 1–3 words ("Cute ilSin" is two), so tags glued to a neighbour's still place well
            var words = l.Words.Count > 0 ? l.Words : [new OcrWord(l.Text, l.Box)];
            for (int i = 0; i < words.Count; i++)
                for (int n = 1; n <= 3 && i + n <= words.Count; n++)
                {
                    var run = words.Skip(i).Take(n).ToList();
                    var text = Clean(string.Concat(run.Select(w => w.Text)));
                    if (text.Length < want.Length - 1) continue;
                    var (at, err) = BestWindow(text, want);
                    if (err > limit || err >= bestErr) continue;
                    bestErr = err;
                    var box = TextBounds.Union(run.Select(w => w.Box));
                    // inside the run, the part that is the name (letters assumed even width)
                    double share = (at + want.Length / 2.0) / text.Length;
                    best = (box.X + box.Width * share, box.Y + box.Height / 2);
                }
        }
        return best;
    }

    static string Clean(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // The substring of text closest to want (edit distance), and where it starts.
    static (int at, int err) BestWindow(string text, string want)
    {
        int bestAt = 0, bestErr = int.MaxValue;
        for (int len = Math.Max(1, want.Length - 1); len <= want.Length + 1; len++)
            for (int i = 0; i + len <= text.Length; i++)
            {
                int e = Distance(text.AsSpan(i, len), want);
                if (e < bestErr) { bestErr = e; bestAt = i; }
            }
        return (bestAt, bestErr);
    }

    static int Distance(ReadOnlySpan<char> a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

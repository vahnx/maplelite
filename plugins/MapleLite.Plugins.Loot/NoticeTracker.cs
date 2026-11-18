using System.Text.RegularExpressions;

namespace MapleLite.Plugins.Loot;

/// <summary>One pickup notice as read: its text, where its row sits (centre, in picture pixels), its row height,
/// and how bright its text is (0–255; notices fade as they age).</summary>
internal sealed record NoticeLine(string Text, double Y, double Height, double Brightness)
{
    /// <summary>What OCR read, when <see cref="Text"/> is a normalized form of it.</summary>
    public string Raw { get; init; } = "";
    public MapleLite.Core.Game.LootDrop? Drop { get; init; }
}

/// <summary>
/// Decides which pickup notices are new since the last read. Text alone can't do it: picking up 10 mesos twice
/// shows two identical lines, and "the same line is still there" looks exactly like "a second one arrived".
/// So each notice on screen is followed as an object with a position. When a notice arrives the others move
/// (the game stacks them), so the tracker finds the one shift that best lines up the notices it knows with the
/// rows it sees now; rows nothing lines up with are new. A row that sits where a known notice was but is clearly
/// brighter is also new: a fresh notice replaced one that had faded.
/// </summary>
internal sealed class NoticeTracker
{
    /// <summary>A known notice missing from this many reads in a row is gone (one miss can be OCR).</summary>
    public const int MissesToForget = 2;
    /// <summary>A row this much brighter (0–255) than the notice that was there is a new notice, not the old one.</summary>
    public const double BrighterBy = 45;

    sealed class Track(NoticeLine line)
    {
        public string Text = line.Text;
        public double Y = line.Y, Brightness = line.Brightness;
        public int Misses;
    }

    readonly List<Track> _tracks = [];

    public int Known => _tracks.Count;

    public void Reset() => _tracks.Clear();

    /// <summary>Takes one read's pickup rows (empty when none) and returns the ones that are new, top to bottom.</summary>
    public List<NoticeLine> Update(IReadOnlyList<NoticeLine> lines)
    {
        var rows = lines.OrderBy(l => l.Y).ToList();
        if (rows.Count == 0)
        {
            foreach (var t in _tracks) t.Misses++;
            _tracks.RemoveAll(t => t.Misses >= MissesToForget);
            return [];
        }

        double tol = Math.Max(4, Median(rows.Select(r => r.Height)) * 0.45);

        // Shifts worth trying: none, and every one that would carry a known notice onto a row with matching text.
        var shifts = new List<double> { 0 };
        foreach (var t in _tracks)
            foreach (var r in rows)
                if (Likeness(t.Text, r.Text) > 0) shifts.Add(r.Y - t.Y);

        (int score, double shift, Track?[] pairs) best = (-1, 0, new Track?[rows.Count]);
        foreach (var d in shifts)
        {
            var (score, pairs) = Match(rows, d, tol);
            if (score > best.score || (score == best.score && Math.Abs(d) < Math.Abs(best.shift))) best = (score, d, pairs);
        }

        var fresh = new List<NoticeLine>();
        var matched = new HashSet<Track>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (best.pairs[i] is { } t)
            {
                t.Text = rows[i].Text; t.Y = rows[i].Y; t.Brightness = rows[i].Brightness; t.Misses = 0;
                matched.Add(t);
            }
            else fresh.Add(rows[i]);
        }
        foreach (var t in _tracks)
            if (!matched.Contains(t)) { t.Y += best.shift; t.Misses++; } // moved with the others but OCR missed it
        _tracks.RemoveAll(t => t.Misses >= MissesToForget);
        foreach (var r in fresh) _tracks.Add(new Track(r));
        return fresh;
    }

    (int score, Track?[] pairs) Match(List<NoticeLine> rows, double shift, double tol)
    {
        var pairs = new Track?[rows.Count];
        var used = new HashSet<Track>();
        int score = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            Track? pick = null; int pickW = 0; double pickDist = double.MaxValue;
            foreach (var t in _tracks)
            {
                if (used.Contains(t)) continue;
                double dist = Math.Abs(t.Y + shift - r.Y);
                if (dist >= tol || r.Brightness > t.Brightness + BrighterBy) continue;
                int w = Likeness(t.Text, r.Text);
                if (w > pickW || (w == pickW && w > 0 && dist < pickDist)) { pick = t; pickW = w; pickDist = dist; }
            }
            if (pick == null) continue;
            used.Add(pick); pairs[i] = pick; score += pickW;
        }
        return (score, pairs);
    }

    /// <summary>2: the same notice text (allowing OCR noise). 1: same words, different numbers (could be a misread
    /// digit on a fading line). 0: different notices.</summary>
    internal static int Likeness(string a, string b)
    {
        // Normalized "Item|name|qty" rows: two reads of one item notice whose name OCR garbled differently
        // (and the catalog couldn't fix) still count as possibly the same notice.
        if (a.StartsWith("Item|") && b.StartsWith("Item|"))
        {
            string na = Letters(Key(a.Split('|')[1])), nb = Letters(Key(b.Split('|')[1]));
            if (na == nb) return a.Split('|')[2] == b.Split('|')[2] ? 2 : 1;
            return Distance(na, nb) <= Math.Max(na.Length, nb.Length) * 0.5 ? 1 : 0;
        }
        string ka = Key(a), kb = Key(b);
        bool sameDigits = Digits(ka) == Digits(kb);
        string la = Letters(ka), lb = Letters(kb);
        int limit = Math.Max(1, (int)(Math.Max(la.Length, lb.Length) * 0.2));
        if (Distance(la, lb) > limit) return 0;
        return sameDigits ? 2 : 1;
    }

    static string Key(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    static string Digits(string s) => string.Concat(s.Where(char.IsDigit));
    static string Letters(string s) => string.Concat(s.Where(c => !char.IsDigit(c)));

    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diag = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diag + (a[i - 1] == b[j - 1] ? 0 : 1));
                diag = old;
            }
        }
        return row[b.Length];
    }

    static double Median(IEnumerable<double> xs)
    {
        var s = xs.Order().ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }
}

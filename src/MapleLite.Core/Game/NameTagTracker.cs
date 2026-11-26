using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>
/// Keeps name tags found by OCR in place on every captured frame, without OCR: each tag's lettering is remembered
/// and looked for again near where it was, so outlines stay on characters between (slow, sometimes missed) reads.
/// <see cref="ForNpcs"/> follows yellow NPC tags. NPCs stand still in the world, so the tag found most clearly gives
/// the camera's move and the others follow it: a covered tag moves with the scene instead of being lost.
/// <see cref="ForPlayers"/> follows white player tags, each on its own (players walk). A tag that can't be seen is
/// held a while. Not thread-safe: call from one thread or under a lock.
/// </summary>
public sealed class NameTagTracker
{
    sealed class Track(string name)
    {
        public string Name { get; } = name;
        public Region Plate;
        /// <summary>Where the lettering is, and the outline around it that must not be lettering, relative to <see cref="Plate"/>.</summary>
        public (int X, int Y)[] Ink = [], Ring = [];
        public int BestInk;
        public DateTimeOffset SeenAt;
        public DateTimeOffset MissedSince;
        /// <summary>Just found, on an OCR frame that may be a second or two old: look further the first time.</summary>
        public bool Fresh = true;
    }

    const int Pad = 2;
    /// <summary>A match this good is the tag beyond doubt, and can stand for the camera's move.</summary>
    const double Sure = 0.8;
    readonly List<Track> _tracks = [];

    /// <summary>Yellow NPC tags, which stay put in the world.</summary>
    public static NameTagTracker ForNpcs() => new() { IsInk = NpcTags.IsYellow, StillWorld = true };

    /// <summary>White player tags. Players walk, so each tag is followed on its own.</summary>
    public static NameTagTracker ForPlayers() => new() { IsInk = p => Pixel.Min(p) >= 235, StillWorld = false, Hold = TimeSpan.FromSeconds(2.5) };

    /// <summary>The tag's lettering color.</summary>
    public Func<int, bool> IsInk { get; init; } = NpcTags.IsYellow;
    /// <summary>Tags stand still in the world (NPCs): they all move together with the camera.</summary>
    public bool StillWorld { get; init; } = true;
    /// <summary>How long a tag that can't be seen keeps its place.</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>How long a tag that can't be seen keeps moving with the camera (read from the other tags).</summary>
    public TimeSpan CoveredHold { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>How far (pixels) a tag may move between two followed frames.</summary>
    public int Reach { get; init; } = 72;
    /// <summary>Score a tag needs on its own when no other tag shows the camera's move.</summary>
    public double MinScore { get; init; } = 0.55;

    public int Count => _tracks.Count;

    /// <summary>Optional: told when a tag starts or stops being followed, and why (for tracing flicker).</summary>
    public Action<string>? Trace { get; set; }

    public void Reset(string why = "reset")
    {
        if (_tracks.Count > 0) Trace?.Invoke($"dropped all ({why})");
        _tracks.Clear();
    }

    /// <summary>Drops tags whose names aren't in <paramref name="names"/> (removed or turned off).</summary>
    public void Keep(IReadOnlyCollection<string> names) =>
        _tracks.RemoveAll(t => !names.Contains(t.Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Takes in what OCR found on <paramref name="frame"/> (which may be older than the frames being followed).
    /// New tags start a track; known ones refresh their lettering when nothing covers it.
    /// </summary>
    public void Confirm(Frame frame, IEnumerable<TagSighting> sightings, DateTimeOffset at)
    {
        foreach (var s in sightings)
        {
            var plate = new Region((int)Math.Floor(s.Plate.X) - Pad, (int)Math.Floor(s.Plate.Y) - Pad,
                (int)Math.Ceiling(s.Plate.Width) + 2 * Pad, (int)Math.Ceiling(s.Plate.Height) + 2 * Pad).Intersect(frame.Bounds);
            var (ink, ring) = Template(frame, plate);
            if (ink.Length < 6) continue;
            var track = _tracks.Where(t => string.Equals(t.Name, s.Name, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(Center(t.Plate).X - Center(plate).X) < Reach * 3 && Math.Abs(Center(t.Plate).Y - Center(plate).Y) < Reach * 2)
                .OrderBy(t => Math.Abs(Center(t.Plate).X - Center(plate).X)).FirstOrDefault();
            if (track == null)
            {
                _tracks.Add(new Track(s.Name) { Plate = plate, Ink = ink, Ring = ring, BestInk = ink.Length, SeenAt = at });
                Trace?.Invoke($"found {s.Name} at {plate}");
                continue;
            }
            if (ink.Length < track.BestInk * 0.9) continue;   // partly covered: keep the lettering we have
            // Keep following from the tracked spot (it is newer), unless the tag's size changed or it was lost a while.
            if (track.Plate.Width != plate.Width || track.Plate.Height != plate.Height || at - track.SeenAt > TimeSpan.FromSeconds(1))
            {
                track.Plate = plate;
                track.Fresh = true;
            }
            track.Ink = ink;
            track.Ring = ring;
            track.BestInk = Math.Max(track.BestInk, ink.Length);
            track.SeenAt = at;
            track.MissedSince = default;
        }
    }

    /// <summary>Finds every known tag again on <paramref name="frame"/>, moving it with the scene.</summary>
    public void Follow(Frame frame, DateTimeOffset at)
    {
        if (_tracks.Count == 0) return;
        var scene = Nameplates.Scene(frame);
        var ink = new sbyte[frame.Px.Length];   // 0 unknown, 1 lettering, -1 not
        var own = _tracks.Select(t =>
        {
            // Small moves first (most frames), the whole reach only when that fails; further for a tag just found.
            var best = Best(frame, ink, t, 0, 0, 8, 6);
            if (best.Score >= Sure) return best;
            int reach = t.Fresh ? Reach * 3 : Reach;
            return Max(best, Best(frame, ink, t, 0, 0, reach, t.Fresh ? Reach : Reach / 2));
        }).ToList();
        int anchor = Enumerable.Range(0, _tracks.Count).MaxBy(i => own[i].Score);
        bool camera = StillWorld && own[anchor].Score >= Sure;
        foreach (var (track, i) in _tracks.Select((t, i) => (t, i)).ToList())
        {
            var (score, dx, dy) = own[i];
            // Enough of the lettering, or part of it right where the tag was (half covered, standing still).
            bool seen = score >= (camera ? Sure : MinScore) || score >= 0.3 && Math.Abs(dx) + Math.Abs(dy) <= 4;
            if (!seen && camera)
            {
                // Move with the camera; the tag counts as seen if enough of it shows there.
                (score, dx, dy) = Best(frame, ink, track, own[anchor].Dx, own[anchor].Dy, 2, 2);
                seen = score >= 0.3;
                if (!seen) (dx, dy) = (own[anchor].Dx, own[anchor].Dy);
            }
            if (seen || camera) track.Plate = track.Plate.Offset(dx, dy);
            if (seen)
            {
                track.SeenAt = at;
                track.MissedSince = default;
                track.Fresh = false;
                continue;
            }
            if (track.MissedSince == default) track.MissedSince = at;
            // A tag already hanging over the screen edge that can't be found has scrolled off.
            bool left = track.Plate.Intersect(scene).Width < track.Plate.Width / 4
                || (track.Plate.Intersect(scene) != track.Plate && at - track.MissedSince >= TimeSpan.FromSeconds(0.2));
            // Otherwise it is covered (a character standing on it, another tag drawn over it): NPCs don't walk off,
            // so while the camera's move is known it moves with the scene; without it, it is held where it was.
            bool covered = DarkShare(frame, track.Plate) >= 0.3;
            var hold = camera ? CoveredHold : covered ? Hold * 2 : Hold;
            if (left || at - track.SeenAt > hold)
            {
                _tracks.Remove(track);
                Trace?.Invoke($"dropped {track.Name} at {track.Plate}: {(left ? "left the scene" : "not seen for " + (at - track.SeenAt).TotalSeconds.ToString("0.0") + " s" + (covered ? " (covered)" : ""))}, own match {own[i].Score:0.00}, camera {(camera ? "known" : "unknown")}");
            }
        }
    }

    /// <summary>The tags being followed, with the box each character stands in.</summary>
    public List<TagSighting> Current(Frame frame)
    {
        var scene = Nameplates.Scene(frame);
        return _tracks.Select(t =>
        {
            var plate = new TextBounds(t.Plate.X + Pad, t.Plate.Y + Pad, t.Plate.Width - 2 * Pad, t.Plate.Height - 2 * Pad);
            return new TagSighting(t.Name, plate, Nameplates.CharacterAbove(plate, scene));
        }).Where(s => !s.Body.IsEmpty).OrderBy(s => s.Plate.X).ToList();
    }

    ((int X, int Y)[] Ink, (int X, int Y)[] Ring) Template(Frame frame, Region box)
    {
        var ink = new List<(int, int)>();
        var ring = new List<(int, int)>();
        bool Ink(int x, int y) => box.Contains(x, y) && IsInk(frame.At(x, y));
        for (int y = box.Y; y < box.Bottom; y++)
            for (int x = box.X; x < box.Right; x++)
            {
                if (Ink(x, y)) ink.Add((x - box.X, y - box.Y));
                else if (Ink(x - 1, y) || Ink(x + 1, y) || Ink(x, y - 1) || Ink(x, y + 1)
                    || Ink(x - 2, y) || Ink(x + 2, y) || Ink(x, y - 2) || Ink(x, y + 2))
                    ring.Add((x - box.X, y - box.Y));
            }
        return (ink.ToArray(), ring.ToArray());
    }

    bool IsInkAt(Frame frame, sbyte[] ink, int i)
    {
        if (ink[i] == 0) ink[i] = IsInk(frame.Px[i]) ? (sbyte)1 : (sbyte)-1;
        return ink[i] > 0;
    }

    /// <summary>
    /// The best place for the tag within (rx, ry) of a move by (cx, cy): the share of its lettering found there,
    /// less half the share of lettering where its outline should be (other text in the way). Ties go to the smaller move.
    /// </summary>
    (double Score, int Dx, int Dy) Best(Frame frame, sbyte[] ink, Track t, int cx, int cy, int rx, int ry)
    {
        if (t.Ink.Length == 0) return (0, 0, 0);
        double best = double.MinValue;
        int bdx = 0, bdy = 0;
        for (int dy = cy - ry; dy <= cy + ry; dy++)
            for (int dx = cx - rx; dx <= cx + rx; dx++)
            {
                int ox = t.Plate.X + dx, oy = t.Plate.Y + dy;
                bool inside = ox >= 0 && oy >= 0 && ox + t.Plate.Width <= frame.W && oy + t.Plate.Height <= frame.H;
                // At the screen's edge only part of the tag shows: score that part, if it's enough to go on.
                int shown = inside ? t.Ink.Length : t.Ink.Count(p => In(frame, ox + p.X, oy + p.Y));
                if (shown < Math.Max(6, t.Ink.Length * 0.4)) continue;
                int hits = 0, misses = 0, give = shown - (int)(shown * 0.3);
                foreach (var (x, y) in t.Ink)
                {
                    if (!inside && !In(frame, ox + x, oy + y)) continue;
                    if (IsInkAt(frame, ink, (oy + y) * frame.W + ox + x)) hits++;
                    else if (++misses > give) break;
                }
                if (misses > give) continue;
                int stray = 0;
                foreach (var (x, y) in t.Ring)
                    if ((inside || In(frame, ox + x, oy + y)) && IsInkAt(frame, ink, (oy + y) * frame.W + ox + x)) stray++;
                double score = (hits - 0.5 * stray) / shown;
                if (score > best + 1e-9 || Math.Abs(score - best) < 1e-9 && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bdx) + Math.Abs(bdy))
                    (best, bdx, bdy) = (score, dx, dy);
            }
        return best == double.MinValue ? (0, cx, cy) : (best, bdx, bdy);
    }

    static bool In(Frame f, int x, int y) => x >= 0 && y >= 0 && x < f.W && y < f.H;

    static (double Score, int Dx, int Dy) Max((double Score, int Dx, int Dy) a, (double Score, int Dx, int Dy) b) => b.Score > a.Score ? b : a;

    /// <summary>Share of the box that is tag backing (dark) or lettering.</summary>
    double DarkShare(Frame frame, Region box)
    {
        box = box.Intersect(frame.Bounds);
        if (box.IsEmpty) return 0;
        int dark = 0;
        for (int y = box.Y; y < box.Bottom; y++)
            for (int x = box.X; x < box.Right; x++)
                if (Pixel.Max(frame.At(x, y)) <= 95 || IsInk(frame.At(x, y))) dark++;
        return (double)dark / (box.Width * box.Height);
    }

    static (double X, double Y) Center(Region r) => (r.X + r.Width / 2.0, r.Y + r.Height / 2.0);
}

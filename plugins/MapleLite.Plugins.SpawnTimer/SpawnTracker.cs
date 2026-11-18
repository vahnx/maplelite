using MapleLite.Core.Game;
using MapleLite.Core.Imaging;

namespace MapleLite.Plugins.SpawnTimer;

/// <summary>
/// How the server brings monsters back (from meowdb's Classic World notes): after a monster dies its spawn point waits
/// <see cref="Cooldown"/>, and the map is refilled on a server-wide tick every <see cref="Tick"/>. So a monster
/// returns between Cooldown and Cooldown + Tick after it died.
/// </summary>
public sealed record RespawnRule(double Cooldown = 5.0, double Tick = 7.56)
{
    public double Earliest => Cooldown;
    public double Latest => Cooldown + Tick;
}

/// <summary>
/// Where one of your kills will come back, in map pixels (see <see cref="CameraTracker"/>): the nearest spawn point
/// MapleLite has seen a monster fade in at (<see cref="AtSpawn"/>), or else where the monster died.
/// </summary>
public sealed record Spot(double MapX, double MapY, DateTimeOffset KilledAt, long Exp)
{
    public int Kills { get; init; } = 1;
    public bool AtSpawn { get; init; }
}

/// <summary>A place monsters have been seen fading in (map pixels; Y is where their feet are).</summary>
public sealed class SpawnPoint
{
    public double MapX { get; set; }
    public double MapY { get; set; }
    public int Seen { get; set; } = 1;
    public double LastSeen { get; set; }
    /// <summary>Seen twice, or once as part of a server refill (several spawn points at once): not a one-off.</summary>
    public bool Confirmed { get; set; }
}

/// <summary>Where a spot's monster is in its respawn: waiting out the cooldown, or due on the next server tick.</summary>
public enum SpotPhase { Cooldown, Due, Expired }

/// <summary>
/// Turns game pictures plus "you gained EXP" moments into spawn spots. Damage numbers are followed as they float up
/// from a monster; when your EXP rises, the newest number near you marks where the monster died. The camera tracker
/// keeps each spot fixed to the map while the view scrolls. Thread-safe: frames, kills and reads may come from
/// different threads.
/// </summary>
public sealed class SpawnTracker
{
    sealed class Track
    {
        public double FirstT, LastT;
        public double BaseX, BaseY;   // where the number first appeared (map), just above the monster
        public double X, Y;           // where it is now (map)
    }

    readonly object _lock = new();
    readonly CameraTracker _camera = new();
    readonly List<Track> _tracks = new();
    readonly List<Spot> _spots = new();
    DateTimeOffset _epoch = DateTimeOffset.MinValue;
    (double X, double Y, double T)? _player;
    int _breaks;
    (DateTimeOffset T, double X, double Y)? _camLast, _camPrev;
    readonly SpawnWatcher _watcher = new();
    readonly List<SpawnPoint> _points = new();
    readonly List<FadeIn> _fades = new();   // recent fade-ins, for spotting server refills
    double? _lastTick;                      // when the server last refilled the map (seconds since _epoch)

    /// <summary>How far (map pixels, sideways) a kill may be from a spawn point and still be timed there. Monsters wander along their platform.</summary>
    public int SnapRange { get; set; } = 260;
    /// <summary>Fade-ins this close together in time, at different places, are one server refill.</summary>
    public double RefillSpread { get; set; } = 0.8;
    /// <summary>How high above a monster's feet to draw its timer.</summary>
    public int BodyHeight { get; set; } = 10; // the fade-in is seen mostly by its outline, so its "feet" already sit a little high

    /// <summary>
    /// Where the camera is at <paramref name="now"/>: the last estimate, carried forward at the speed the view was scrolling
    /// (for at most a quarter second). Pictures arrive a few times a second while the game scrolls smoothly; without this,
    /// timers drawn over the game would lag and then jump.
    /// </summary>
    (double X, double Y) CameraAt(DateTimeOffset now)
    {
        if (_camLast is not { } last) return (_camera.X, _camera.Y);
        if (_camPrev is not { } prev) return (last.X, last.Y);
        double span = (last.T - prev.T).TotalSeconds;
        if (span <= 0 || span > 0.5) return (last.X, last.Y);
        double ahead = Math.Clamp((now - last.T).TotalSeconds, 0, 0.25);
        return (last.X + (last.X - prev.X) / span * ahead, last.Y + (last.Y - prev.Y) / span * ahead);
    }

    public RespawnRule Rule { get; set; } = new();
    /// <summary>How far below the bottom of a damage number to put the spot (the monster sits under its numbers).</summary>
    public int SpotDrop { get; set; } = 70;
    /// <summary>How close a kill must be to an earlier spot to count as the same spawn point.</summary>
    public int SameSpot { get; set; } = 70;
    /// <summary>How far back from an EXP rise to look for the killing hit. EXP arrives about 2 s after the monster dies.</summary>
    public double KillWindow { get; set; } = 3.5;

    public bool CameraLocked { get { lock (_lock) return _camera.Locked; } }
    public (double X, double Y) Camera { get { lock (_lock) return (_camera.X, _camera.Y); } }
    public int KillsMarked { get; private set; }
    public int KillsMissed { get; private set; }
    /// <summary>Times the view was lost (map change, loading screen) and the timers were cleared.</summary>
    public int Resets { get; private set; }
    /// <summary>What the last <see cref="Kill"/> had to choose from (for the panel's details and for tuning).</summary>
    public string LastKillNote { get; private set; } = "";

    double T(DateTimeOffset t) => (t - _epoch).TotalSeconds;

    /// <summary>Adds a game picture taken at <paramref name="time"/>.</summary>
    public void AddFrame(Frame f, DateTimeOffset time)
    {
        var numbers = DamageNumbers.Find(f);
        lock (_lock)
        {
            if (_epoch == DateTimeOffset.MinValue) _epoch = time;
            _camera.Add(f);
            _camPrev = _camLast;
            _camLast = (time, _camera.X, _camera.Y);
            if (_camera.Breaks != _breaks)
            {
                // the view jumped (a new map, a loading screen): old spots no longer line up
                _breaks = _camera.Breaks;
                _spots.Clear(); _tracks.Clear(); _player = null; _camPrev = null;
                _watcher.Reset(); _points.Clear(); _fades.Clear(); _lastTick = null;
                Resets++;
            }
            double t = T(time);
            if (_camera.Locked)
                foreach (var fade in _watcher.Add(f, t, _camera.X, _camera.Y)) OnFadeIn(fade);
            foreach (var r in numbers)
            {
                double mx = r.X + r.Width / 2.0 + _camera.X, my = r.Y + r.Height + _camera.Y;
                // the same number one picture later: about the same x, a little higher up
                var tr = _tracks.FirstOrDefault(k => t - k.LastT < 0.5 && Math.Abs(k.X - mx) < 30 && my <= k.Y + 10 && my >= k.Y - 60);
                if (tr == null) _tracks.Add(new Track { FirstT = t, LastT = t, BaseX = mx, BaseY = my, X = mx, Y = my });
                else { tr.LastT = t; tr.X = mx; tr.Y = my; }
            }
            _tracks.RemoveAll(k => t - k.FirstT > KillWindow + 1);
        }
    }

    // A monster faded in: learn (or strengthen) the spawn point, note server refills, and end the timer waiting on it.
    internal void OnFadeIn(FadeIn fade) // internal for tests; called under _lock
    {
        FadesSeen++;
        var point = _points.FirstOrDefault(p => Math.Abs(p.MapX - fade.MapX) < 30 && Math.Abs(p.MapY - fade.MapY) < 24);
        if (point == null) _points.Add(point = new SpawnPoint { MapX = fade.MapX, MapY = fade.MapY, LastSeen = fade.T });
        else
        {
            point.MapX = (point.MapX * point.Seen + fade.MapX) / (point.Seen + 1);
            point.MapY = (point.MapY * point.Seen + fade.MapY) / (point.Seen + 1);
            point.Seen++; point.LastSeen = fade.T; point.Confirmed = true;
        }
        // several spawn points filling at once is the server's refill tick
        _fades.RemoveAll(e => fade.T - e.T > RefillSpread);
        var together = _fades.Where(e => Math.Abs(e.MapX - fade.MapX) > 40 || Math.Abs(e.MapY - fade.MapY) > 40).ToList();
        if (together.Count > 0)
        {
            double tick = Math.Min(fade.T, together.Min(e => e.T));
            if (_lastTick is not { } lt || tick - lt > RefillSpread)
            {
                if (_lastTick is { } prev) LearnPeriod(tick - prev);
                _lastTick = tick; Refills++;
            }
            foreach (var e in together)
                if (_points.FirstOrDefault(p => Math.Abs(p.MapX - e.MapX) < 30 && Math.Abs(p.MapY - e.MapY) < 24) is { } q) q.Confirmed = true;
            point.Confirmed = true;
        }
        _fades.Add(fade);
        // the monster a timer was waiting for is back
        _spots.RemoveAll(s => s.AtSpawn && Math.Abs(s.MapX - point.MapX) < 30 && Math.Abs(s.MapY + BodyHeight - point.MapY) < 30
                              && fade.T - T(s.KilledAt) > Rule.Cooldown - 1);
    }

    readonly List<double> _periods = new();

    // The gap between two refills is a whole number of periods (refills with nothing to fill go unseen).
    void LearnPeriod(double gap)
    {
        double guess = TickPeriod;
        int k = (int)Math.Round(gap / guess);
        if (k < 1 || k > 6) return;
        double p = gap / k;
        if (Math.Abs(p - guess) > guess * 0.2) return;
        _periods.Add(p);
        if (_periods.Count > 15) _periods.RemoveAt(0);
    }

    /// <summary>The refill period in use: measured from refills once three gaps agree well enough, else the setting.</summary>
    public double TickPeriod { get { lock (_lock) return _periods.Count >= 3 ? _periods.Order().ElementAt(_periods.Count / 2) : Rule.Tick; } }
    public bool TickMeasured { get { lock (_lock) return _periods.Count >= 3; } }

    /// <summary>Monster fade-ins seen, and how many of them were server refills (several at once).</summary>
    public int FadesSeen { get; private set; }
    public int Refills { get; private set; }
    public int SpawnPointsKnown { get { lock (_lock) return _points.Count(p => p.Confirmed); } }
    /// <summary>Seconds since the server last refilled the map, if one has been seen.</summary>
    public double? SinceRefill(DateTimeOffset now) { lock (_lock) return _lastTick is { } lt ? T(now) - lt : null; }

    /// <summary>Learned spawn points, on screen now (feet position).</summary>
    public List<(double ScreenX, double ScreenY, int Seen)> SpawnPoints(DateTimeOffset now)
    {
        lock (_lock)
        {
            var (cx, cy) = CameraAt(now);
            return _points.Where(p => p.Confirmed).Select(p => (Math.Round(p.MapX - cx), Math.Round(p.MapY - cy), p.Seen)).ToList();
        }
    }

    /// <summary>Where your character's name tag was seen (screen position in the picture), at <paramref name="time"/>.</summary>
    public void SetPlayer(double screenX, double screenY, DateTimeOffset time)
    {
        lock (_lock) _player = (screenX + _camera.X, screenY + _camera.Y, T(time));
    }

    public (double X, double Y)? PlayerOnScreen(DateTimeOffset now)
    {
        lock (_lock) return _player is { } p && T(now) - p.T < 5 ? (p.X - _camera.X, p.Y - _camera.Y) : null;
    }

    /// <summary>Your EXP rose at <paramref name="time"/>: mark where the monster died. Returns the spot, or null if no damage number fits.</summary>
    public Spot? Kill(DateTimeOffset time, long exp)
    {
        lock (_lock)
        {
            if (_epoch == DateTimeOffset.MinValue) return null;
            double t = T(time);
            var candidates = _tracks.Where(k => k.FirstT >= t - KillWindow && k.FirstT <= t + 0.3).ToList();
            LastKillNote = $"player {(_player is { } pp ? $"({pp.X:0},{pp.Y:0}) {t - pp.T:0.0}s ago" : "unknown")}; numbers "
                + string.Join(", ", candidates.Select(k => $"({k.BaseX:0},{k.BaseY:0}) {t - k.FirstT:0.0}s ago"));
            if (_player is { } p && t - p.T < 5)
                // your own hits land in front of you, on your floor: a screen-ish distance sideways, numbers above your feet
                candidates = candidates.Where(k => Math.Abs(k.BaseX - p.X) <= 650 && p.Y - k.BaseY >= -40 && p.Y - k.BaseY <= 300).ToList();
            if (candidates.Count == 0) { KillsMissed++; return null; }
            // EXP arrives 0.5–2.5 s after the death, and by then you may be hitting the next monster. Group the hits by
            // spot (one monster each); the dead one is the spot whose hits stopped last before the EXP came in.
            var groups = new List<List<Track>>();
            foreach (var k in candidates.OrderBy(k => k.FirstT))
            {
                var g = groups.FirstOrDefault(g => Math.Abs(g[^1].BaseX - k.BaseX) < 50 && Math.Abs(g[^1].BaseY - k.BaseY) < 50);
                if (g == null) groups.Add([k]); else g.Add(k);
            }
            var stopped = groups.Where(g => g[^1].FirstT <= t - 0.3).ToList();
            var dead = (stopped.Count > 0 ? stopped : groups).MaxBy(g => g[^1].FirstT)!;
            var last = dead[^1];
            var spot = new Spot(last.BaseX, last.BaseY + SpotDrop, _epoch.AddSeconds(last.FirstT), exp);
            // It comes back at its spawn point, not where it died: the nearest learned one on the same platform.
            var home = _points.Where(p => p.Confirmed && Math.Abs(p.MapX - spot.MapX) <= SnapRange && Math.Abs(p.MapY - BodyHeight - spot.MapY) <= 60)
                .MinBy(p => Math.Abs(p.MapX - spot.MapX));
            if (home != null) spot = spot with { MapX = home.MapX, MapY = home.MapY - BodyHeight, AtSpawn = true };
            int same = _spots.FindIndex(s => Math.Abs(s.MapX - spot.MapX) < SameSpot && Math.Abs(s.MapY - spot.MapY) < SameSpot);
            // one timer per spawn point; for death spots, a second kill nearby replaces the first
            if (same >= 0 && (!spot.AtSpawn || (Math.Abs(_spots[same].MapX - spot.MapX) < 1 && Math.Abs(_spots[same].MapY - spot.MapY) < 1)))
            { spot = spot with { Kills = _spots[same].Kills + 1 }; _spots.RemoveAt(same); }
            _spots.Add(spot);
            foreach (var k in dead) _tracks.Remove(k); // those hits are used up
            KillsMarked++;
            return spot;
        }
    }

    /// <summary>Spots still waiting on a respawn, with where they are on screen now (may be off screen).</summary>
    /// <remarks>
    /// <see cref="SpotPhase.Cooldown"/> counts down to the earliest moment the monster can be back. Once a server refill
    /// has been seen recently, that moment is the exact refill after the cooldown (<c>Exact</c>), and <see cref="SpotPhase.Due"/>
    /// lasts only a moment; otherwise it's the end of the cooldown, and Due spans one refill period.
    /// </remarks>
    public List<(Spot Spot, double ScreenX, double ScreenY, SpotPhase Phase, double Seconds, double Share, bool Exact)> Active(DateTimeOffset now)
    {
        lock (_lock)
        {
            var rule = Rule;
            var (cx, cy) = CameraAt(now);
            cx = Math.Round(cx); cy = Math.Round(cy); // whole pixels: sub-pixel steps make drawn rings shimmer
            double tNow = T(now);
            double? DueAt(Spot s)
            {
                // the first refill after the cooldown ends, counted from the last refill seen (if it's recent enough to trust)
                double period = TickPeriod;
                if (_lastTick is not { } lt || tNow - lt > 4 * period || period <= 0) return null;
                double ready = T(s.KilledAt) + rule.Cooldown;
                return lt + Math.Max(0, Math.Ceiling((ready - lt) / period)) * period;
            }
            _spots.RemoveAll(s => DueAt(s) is { } due ? tNow - due > 2 : (now - s.KilledAt).TotalSeconds > rule.Latest + 1.5);
            return _spots.Select(s =>
            {
                double age = (now - s.KilledAt).TotalSeconds;
                if (DueAt(s) is { } due)
                {
                    double total = Math.Max(0.1, due - T(s.KilledAt)), left = due - tNow;
                    return left > 0
                        ? (s, Math.Round(s.MapX) - cx, Math.Round(s.MapY) - cy, SpotPhase.Cooldown, left, Math.Clamp(age / total, 0, 1), true)
                        : (s, Math.Round(s.MapX) - cx, Math.Round(s.MapY) - cy, SpotPhase.Due, 0.0, 1.0, true);
                }
                var (phase, l2, share) = age < rule.Earliest ? (SpotPhase.Cooldown, rule.Earliest - age, age / rule.Earliest)
                    : age < rule.Latest ? (SpotPhase.Due, rule.Latest - age, (age - rule.Earliest) / rule.Tick)
                    : (SpotPhase.Expired, 0.0, 1.0);
                return (s, Math.Round(s.MapX) - cx, Math.Round(s.MapY) - cy, phase, l2, share, false);
            }).ToList();
        }
    }

    public void ClearSpots() { lock (_lock) _spots.Clear(); }
    /// <summary>Forgets learned spawn points too (they're relearned as monsters fade in).</summary>
    public void ClearAll() { lock (_lock) { _spots.Clear(); _points.Clear(); _fades.Clear(); _lastTick = null; _periods.Clear(); } }
}

namespace MapleLite.Plugins.XpTracker;

/// <summary>
/// One hunting session, fed with status-bar readings. Counts EXP gained, rises in EXP (≈ kills), the rate,
/// and the time to the next level. A drop in EXP (level-up or death) is only believed when the next reading agrees,
/// so a single bad read can't wipe the totals.
/// </summary>
public sealed class XpSession
{
    readonly XpRate _rate;
    long? _pendingDrop;

    public XpSession(TimeSpan? window = null) => _rate = new XpRate(window);

    public DateTimeOffset? Started => _rate.Started;
    public DateTimeOffset? LastTime { get; private set; }
    public long? Exp { get; private set; }
    public double? Percent { get; private set; }
    public long Gained => _rate.Gained;
    public int Levels => _rate.Levels;
    /// <summary>Rises in EXP seen. Kills close together can share one.</summary>
    public int Gains { get; private set; }
    public long? LastGain { get; private set; }
    public double? PerHour => _rate.PerHour;

    public TimeSpan Elapsed => Started is { } s && LastTime is { } l ? l - s : TimeSpan.Zero;

    public double? GainsPerHour => Elapsed.TotalMinutes >= 1 ? Gains / Elapsed.TotalHours : null;

    /// <summary>EXP the current level needs in all, worked out from the EXP and the percent.</summary>
    public long? Needed => Exp is { } e && Percent is >= 1 and < 100 ? (long)Math.Round(e * 100 / Percent.Value) : null;

    public long? ToLevel => Needed is { } n && Exp is { } e ? Math.Max(0, n - e) : null;

    public TimeSpan? TimeToLevel
    {
        get
        {
            if (ToLevel is not { } left || PerHour is not { } rate || rate <= 0) return null;
            var hours = left / rate;
            return hours > 24 * 30 ? null : TimeSpan.FromHours(hours);
        }
    }

    /// <summary>How far a rise's EXP-per-percent may stray from the level's before the reading is called a misread.</summary>
    const double NeededTolerance = 0.03;
    /// <summary>Readings in a row that must agree on an out-of-line value before the session takes it as the truth.</summary>
    const int ResyncReadings = 15;

    long? _suspect;
    int _suspectCount;

    /// <summary>Adds a reading. Returns the rise in EXP it showed (0 when unchanged or not yet believed).</summary>
    public long Add(DateTimeOffset t, long exp, double percent)
    {
        LastTime = t;
        long gain = 0;
        if (Exp is { } last && exp < last)
        {
            if (_pendingDrop != exp) { _pendingDrop = exp; return 0; } // wait for a second reading to agree
        }
        else if (Exp is { } prev && exp > prev)
        {
            // A rise never changes the level, so the EXP the level needs (EXP ÷ percent) must stay the same.
            // A garbled read (the bar half covered by another window) breaks that: hold it, and only take it
            // as the truth, without counting it as EXP gained, if it stays put for a few seconds.
            if (Needed is { } need && NeededFor(exp, percent) is { } n && Math.Abs(n - need) > need * NeededTolerance)
            {
                _suspectCount = _suspect == exp ? _suspectCount + 1 : 1;
                _suspect = exp;
                if (_suspectCount >= ResyncReadings) Resync(t, exp, percent);
                return 0;
            }
            gain = exp - prev;
            Gains++;
            LastGain = gain;
        }
        _pendingDrop = null;
        _suspect = null;
        Exp = exp;
        Percent = percent;
        _rate.Add(t, new ExpReading(exp, percent, null));
        return gain;
    }

    static long? NeededFor(long exp, double percent) => percent is >= 1 and < 100 ? (long)Math.Round(exp * 100 / percent) : null;

    void Resync(DateTimeOffset t, long exp, double percent)
    {
        _pendingDrop = null; _suspect = null;
        Exp = exp;
        Percent = percent;
        _rate.Rebase(t, new ExpReading(exp, percent, null));
    }
}

/// <summary>A finished session, as kept in the history.</summary>
public sealed record XpSessionRecord(DateTimeOffset Started, DateTimeOffset Ended, long Gained, int Gains, int Levels,
    double StartPercent, double EndPercent)
{
    public double PerHour => (Ended - Started).TotalHours is > 0 and var h ? Gained / h : 0;
}

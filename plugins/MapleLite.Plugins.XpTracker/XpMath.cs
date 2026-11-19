using System.Globalization;
using System.Text.RegularExpressions;

namespace MapleLite.Plugins.XpTracker;

/// <summary>One EXP reading from the status bar.</summary>
public readonly record struct ExpReading(long Exp, double? Percent, long? Needed);

/// <summary>
/// Reads EXP text as OCR returns it from the status bar. Accepts the shapes seen across clients:
/// "EXP 12345 [12.34%]", "12345[12.34%]", "EXP 12,345 / 50,000", "12345 (12.34%)".
/// Common OCR slips are tolerated (O for 0, l/I for 1, stray spaces inside numbers).
/// </summary>
public static partial class ExpParser
{
    [GeneratedRegex(@"(?<exp>\d[\d,\. ]*?)\s*(?:/\s*(?<need>\d[\d,\. ]*?)\s*)?(?:[\[\(]\s*(?<pct>\d{1,3}(?:[\.,]\d{1,3})?)\s*%\s*[\]\)]?)?\s*$")]
    private static partial Regex Shape();

    public static ExpReading? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Regex.Replace(text, @"(?i)\bexp\b\.?", " ");
        t = Regex.Replace(t, @"(?<=\d)[Oo]|[Oo](?=\d)", "0");
        t = Regex.Replace(t, @"(?<=\d)[lI|]|[lI|](?=\d)", "1");
        var m = Shape().Match(t.Trim());
        if (!m.Success) return null;
        long? exp = Number(m.Groups["exp"].Value);
        if (exp == null) return null;
        long? need = m.Groups["need"].Success ? Number(m.Groups["need"].Value) : null;
        double? pct = m.Groups["pct"].Success && double.TryParse(m.Groups["pct"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p <= 100 ? p : null;
        if (pct == null && need is > 0) pct = exp.Value * 100.0 / need.Value;
        return new ExpReading(exp.Value, pct, need);
    }

    static long? Number(string s)
    {
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 15 && long.TryParse(digits, out var v) ? v : null;
    }
}

/// <summary>
/// EXP rate over a sliding window, and time to the next level. A drop in EXP is a level-up (or death):
/// the session total keeps the EXP gained before the drop.
/// </summary>
public sealed class XpRate
{
    readonly TimeSpan _window;
    readonly List<(DateTimeOffset t, long gained, double? pct)> _samples = new();
    long _gainedBeforeDrop, _last = -1, _sessionStart = -1;

    public XpRate(TimeSpan? window = null) => _window = window ?? TimeSpan.FromMinutes(10);

    public DateTimeOffset? Started { get; private set; }
    public int Levels { get; private set; }
    public long Gained => _last < 0 ? 0 : _gainedBeforeDrop + (_last - _sessionStart);
    public double? LastPercent { get; private set; }

    public void Add(DateTimeOffset t, ExpReading r)
    {
        Started ??= t;
        if (_last < 0) { _last = r.Exp; _sessionStart = r.Exp; }
        else if (r.Exp < _last)
        {
            // level-up (or a death penalty): bank what was gained so far and start counting from the new value
            _gainedBeforeDrop += _last - _sessionStart;
            _sessionStart = r.Exp;
            if (LastPercent is > 80 && r.Percent is < 20) Levels++;
        }
        _last = r.Exp;
        LastPercent = r.Percent ?? LastPercent;
        _samples.Add((t, Gained, r.Percent));
        while (_samples.Count > 2 && t - _samples[0].t > _window) _samples.RemoveAt(0);
    }

    /// <summary>Takes a new EXP value as the starting point without counting the change as gained.</summary>
    public void Rebase(DateTimeOffset t, ExpReading r)
    {
        Started ??= t;
        if (_last >= 0) _gainedBeforeDrop += _last - _sessionStart;
        _last = _sessionStart = r.Exp;
        LastPercent = r.Percent ?? LastPercent;
        _samples.Add((t, Gained, r.Percent));
    }

    /// <summary>EXP per hour over the window, or null before there are two samples a few seconds apart.</summary>
    public double? PerHour
    {
        get
        {
            if (_samples.Count < 2) return null;
            var (t0, g0, _) = _samples[0]; var (t1, g1, _) = _samples[^1];
            double hours = (t1 - t0).TotalHours;
            return hours < 5.0 / 3600 ? null : (g1 - g0) / hours;
        }
    }

    /// <summary>Percent of a level per hour, from percent readings in the window (same level only).</summary>
    public double? PercentPerHour
    {
        get
        {
            var pts = _samples.Where(s => s.pct != null).ToList();
            if (pts.Count < 2) return null;
            int start = pts.Count - 1;
            while (start > 0 && pts[start - 1].pct <= pts[start].pct) start--; // stay within the current level
            var (t0, _, p0) = pts[start]; var (t1, _, p1) = pts[^1];
            double hours = (t1 - t0).TotalHours;
            return hours < 5.0 / 3600 ? null : (p1!.Value - p0!.Value) / hours;
        }
    }

    public TimeSpan? TimeToLevel
    {
        get
        {
            if (LastPercent is not { } pct || PercentPerHour is not { } rate || rate <= 0) return null;
            var hours = (100 - pct) / rate;
            return hours > 24 * 30 ? null : TimeSpan.FromHours(hours);
        }
    }
}

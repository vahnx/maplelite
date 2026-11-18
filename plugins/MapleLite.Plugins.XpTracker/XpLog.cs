using System.Globalization;
using System.Text;
using MapleLite.Sdk;

namespace MapleLite.Plugins.XpTracker;

/// <summary>One rise in EXP: when, how much, and the map you were on ("" when the map wasn't readable).</summary>
public readonly record struct XpGain(DateTimeOffset Time, long Amount, string Map);

/// <summary>
/// Every EXP gain, kept for browsing by map and time. Stored as one small tab-separated file per day
/// (<c>gains/2026-11-18.tsv</c>: time, amount, map), appended in batches so a crash loses at most a few seconds.
/// Older monthly files are split into days the first time the log opens.
/// </summary>
public sealed class XpLog
{
    readonly string _folder;
    readonly object _lock = new();
    readonly object _write = new(); // one writer or reader of the files at a time
    readonly List<XpGain> _pending = new();
    List<XpGain>? _all;

    public XpLog(string folder)
    {
        _folder = Path.Combine(folder, "gains");
        DailyFiles.SplitMonthly(_folder, "", ".tsv", line => Parse(line)?.Time);
    }

    public void Add(XpGain g) { lock (_lock) { _pending.Add(g); _all?.Add(g); } }

    /// <summary>Writes gains not yet on disk.</summary>
    public void Flush()
    {
        lock (_write) FlushLocked();
    }

    void FlushLocked()
    {
        List<XpGain> batch;
        lock (_lock) { if (_pending.Count == 0) return; batch = [.. _pending]; _pending.Clear(); }
        Directory.CreateDirectory(_folder);
        foreach (var day in batch.GroupBy(g => DailyFiles.PathFor(_folder, "", ".tsv", g.Time)))
        {
            var sb = new StringBuilder();
            foreach (var g in day)
                sb.Append(g.Time.ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                  .Append(g.Amount.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(g.Map.Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
            File.AppendAllText(day.Key, sb.ToString());
        }
    }

    /// <summary>All gains, oldest first (read from disk once, then kept up to date).</summary>
    public IReadOnlyList<XpGain> All()
    {
        lock (_write) lock (_lock)
        {
            if (_all != null) return [.. _all];
            var list = new List<XpGain>();
            if (Directory.Exists(_folder))
                foreach (var file in Directory.GetFiles(_folder, "*.tsv").Order())
                    foreach (var line in File.ReadLines(file))
                        if (Parse(line) is { } g) list.Add(g);
            // gains added since start that aren't flushed yet are already in _pending; the file holds the rest
            foreach (var p in _pending) if (!list.Contains(p)) list.Add(p);
            list.Sort((a, b) => a.Time.CompareTo(b.Time));
            _all = list;
            return [.. list];
        }
    }

    internal static XpGain? Parse(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length < 2) return null;
        if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) return null;
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)) return null;
        return new XpGain(t, amount, parts.Length > 2 ? parts[2] : "");
    }
}

/// <summary>
/// Keeps map names steady across days: a name that is a slip away from one already known ("Henesys Hunting Ground l")
/// is taken as that name, so one map doesn't show up under several spellings. (MapleLite's shared map watcher already
/// waits for two reads to agree before it changes the map.)
/// </summary>
public sealed class MapNames
{
    readonly List<string> _known = new();
    public string Current { get; private set; } = "";

    public MapNames(IEnumerable<string>? known = null)
    {
        if (known != null) _known.AddRange(known.Where(k => k.Length > 0).Distinct());
    }

    /// <summary>The map watcher's current name (null while unknown: the last name is kept). Returns true when the map changed.</summary>
    public bool Add(string? read)
    {
        if (string.IsNullOrWhiteSpace(read)) return false;
        var name = Canonical(read.Trim());
        if (name == Current) return false;
        Current = name;
        if (!_known.Contains(name)) _known.Add(name);
        return true;
    }

    string Canonical(string read)
    {
        string? best = null;
        int bestD = int.MaxValue;
        foreach (var k in _known)
        {
            int d = Distance(Key(k), Key(read));
            if (d < bestD) { bestD = d; best = k; }
        }
        return best != null && bestD <= Math.Max(2, Key(read).Length / 10) ? best : read;
    }

    static string Key(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static int Distance(string a, string b)
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

public enum XpGrouping { Map, Day, Hour, MapAndDay }

/// <summary>One row of the browse table.</summary>
public sealed record XpSummary(string Label, string Map, DateTimeOffset First, DateTimeOffset Last, long Exp, int Kills, TimeSpan Hunting)
{
    public double? PerHour => Hunting.TotalMinutes >= 1 ? Exp / Hunting.TotalHours : null;
    public double? KillsPerHour => Hunting.TotalMinutes >= 1 ? Kills / Hunting.TotalHours : null;
    public double AveragePerKill => Kills > 0 ? (double)Exp / Kills : 0;
}

/// <summary>Filters and groups logged gains for browsing.</summary>
public static class XpBrowse
{
    /// <summary>Gaps between gains longer than this are breaks (town, AFK, a loading screen), not hunting time.</summary>
    public static readonly TimeSpan BreakAfter = TimeSpan.FromMinutes(2);

    /// <summary>Gains on <paramref name="map"/> (null for all) between <paramref name="from"/> and <paramref name="to"/> (local dates, inclusive; null for open).</summary>
    public static List<XpGain> Filter(IEnumerable<XpGain> gains, string? map, DateTime? from, DateTime? to) =>
        gains.Where(g => (map == null || g.Map == map)
                         && (from == null || g.Time.LocalDateTime.Date >= from.Value.Date)
                         && (to == null || g.Time.LocalDateTime.Date <= to.Value.Date)).ToList();

    /// <summary>Hunting time: the gaps between consecutive gains on the same map, each counted up to <see cref="BreakAfter"/>.</summary>
    public static TimeSpan Hunting(IEnumerable<XpGain> gains)
    {
        var total = TimeSpan.Zero;
        XpGain? prev = null;
        foreach (var g in gains.OrderBy(g => g.Time))
        {
            if (prev is { } p && p.Map == g.Map)
            {
                var gap = g.Time - p.Time;
                if (gap > TimeSpan.Zero && gap <= BreakAfter) total += gap;
            }
            prev = g;
        }
        return total;
    }

    public static List<XpSummary> Group(IEnumerable<XpGain> gains, XpGrouping by)
    {
        static string Map(XpGain g) => g.Map.Length > 0 ? g.Map : "Unknown map";
        static string Day(XpGain g) => g.Time.LocalDateTime.ToString("ddd MMM d yyyy", CultureInfo.CurrentCulture);
        var groups = by switch
        {
            XpGrouping.Map => gains.GroupBy(g => (Key: Map(g), Label: Map(g))),
            XpGrouping.Day => gains.GroupBy(g => (Key: g.Time.LocalDateTime.ToString("yyyy-MM-dd"), Label: Day(g))),
            XpGrouping.Hour => gains.GroupBy(g => (Key: g.Time.LocalDateTime.ToString("yyyy-MM-dd HH"), Label: g.Time.LocalDateTime.ToString("ddd MMM d, HH:00", CultureInfo.CurrentCulture))),
            _ => gains.GroupBy(g => (Key: g.Time.LocalDateTime.ToString("yyyy-MM-dd") + "|" + Map(g), Label: Day(g) + " · " + Map(g))),
        };
        var rows = groups.Select(gr =>
        {
            var list = gr.OrderBy(g => g.Time).ToList();
            var maps = list.Select(Map).Distinct().ToList();
            return (gr.Key.Key, Row: new XpSummary(gr.Key.Label, maps.Count == 1 ? maps[0] : $"{maps.Count} maps", list[0].Time, list[^1].Time,
                list.Sum(g => g.Amount), list.Count, Hunting(list)));
        });
        // maps by most EXP; times newest first
        return (by == XpGrouping.Map ? rows.OrderByDescending(r => r.Row.Exp) : rows.OrderByDescending(r => r.Key)).Select(r => r.Row).ToList();
    }

    public static XpSummary Total(IReadOnlyCollection<XpGain> gains) =>
        new("Total", "", gains.Count > 0 ? gains.Min(g => g.Time) : default, gains.Count > 0 ? gains.Max(g => g.Time) : default,
            gains.Sum(g => g.Amount), gains.Count, Hunting(gains));
}

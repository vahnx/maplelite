using System.Globalization;
using System.Text.RegularExpressions;

namespace MapleLite.Sdk;

/// <summary>
/// One log file per day: <c>&lt;prefix&gt;YYYY-MM-DD&lt;ext&gt;</c>, dated by the player's local day.
/// Small files are quick to open, back up, share or delete one day at a time.
/// </summary>
public static class DailyFiles
{
    /// <summary>Folder that monthly files are moved into once they've been split into days.</summary>
    public const string MonthlyBackupDir = "monthly-backup";

    /// <summary>The local calendar day of <paramref name="time"/>, as YYYY-MM-DD.</summary>
    public static string Day(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The day's file, e.g. <c>Path(folder, "loot-", ".jsonl", t)</c> → <c>folder/loot-2026-11-18.jsonl</c>.</summary>
    public static string PathFor(string folder, string prefix, string ext, DateTimeOffset time) =>
        System.IO.Path.Combine(folder, prefix + Day(time) + ext);

    /// <summary>
    /// Splits older monthly files (<c>&lt;prefix&gt;YYYY-MM&lt;ext&gt;</c>) in <paramref name="folder"/> into daily ones,
    /// then moves each monthly file into <see cref="MonthlyBackupDir"/>; nothing is deleted. Lines already in a daily
    /// file aren't added twice, so an interrupted split can safely run again. Lines whose time can't be read go to
    /// <c>&lt;prefix&gt;YYYY-MM-unsorted&lt;ext&gt;</c>. Returns how many monthly files were split.
    /// </summary>
    public static int SplitMonthly(string folder, string prefix, string ext, Func<string, DateTimeOffset?> timeOf)
    {
        if (!Directory.Exists(folder)) return 0;
        var monthly = new Regex("^" + Regex.Escape(prefix) + @"(\d{4}-\d{2})" + Regex.Escape(ext) + "$");
        int split = 0;
        foreach (var path in Directory.GetFiles(folder).Order())
        {
            var m = monthly.Match(System.IO.Path.GetFileName(path));
            if (!m.Success) continue;
            var byFile = new Dictionary<string, List<string>>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                var dst = timeOf(line) is { } t ? PathFor(folder, prefix, ext, t)
                        : System.IO.Path.Combine(folder, prefix + m.Groups[1].Value + "-unsorted" + ext);
                if (!byFile.TryGetValue(dst, out var list)) byFile[dst] = list = [];
                list.Add(line);
            }
            foreach (var (dst, lines) in byFile)
            {
                var have = File.Exists(dst) ? new HashSet<string>(File.ReadLines(dst)) : [];
                var fresh = lines.Where(have.Add).ToList();
                if (fresh.Count > 0) File.AppendAllLines(dst, fresh);
            }
            var backup = Directory.CreateDirectory(System.IO.Path.Combine(folder, MonthlyBackupDir)).FullName;
            var target = System.IO.Path.Combine(backup, System.IO.Path.GetFileName(path));
            if (File.Exists(target)) target += "." + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(path, target);
            split++;
        }
        return split;
    }
}

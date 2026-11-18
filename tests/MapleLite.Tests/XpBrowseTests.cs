using MapleLite.Plugins.XpTracker;
using Xunit;

namespace MapleLite.Tests;

public class XpBrowseTests
{
    static readonly DateTimeOffset Day1 = new(2026, 11, 17, 20, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 17, 20, 0, 0)));

    static List<XpGain> Sample()
    {
        var g = new List<XpGain>();
        // Henesys: a gain every 10 s for 5 minutes (30 gains of 12)
        for (int i = 0; i < 30; i++) g.Add(new XpGain(Day1.AddSeconds(i * 10), 12, "Henesys Hunting Ground I"));
        // a 20-minute break, then Ant Tunnel: every 20 s for 2 minutes (6 gains of 50), the next day
        for (int i = 0; i < 6; i++) g.Add(new XpGain(Day1.AddDays(1).AddSeconds(i * 20), 50, "Ant Tunnel I"));
        return g;
    }

    [Fact]
    public void Hunting_time_skips_breaks_and_map_changes()
    {
        var g = Sample();
        Assert.Equal(TimeSpan.FromSeconds(290), XpBrowse.Hunting(g.Take(30)));
        Assert.Equal(TimeSpan.FromSeconds(290 + 100), XpBrowse.Hunting(g)); // the day-long gap isn't hunting
    }

    [Fact]
    public void Grouped_by_map_with_rates()
    {
        var rows = XpBrowse.Group(Sample(), XpGrouping.Map);
        Assert.Equal(["Henesys Hunting Ground I", "Ant Tunnel I"], rows.Select(r => r.Label));
        var h = rows[0];
        Assert.Equal(360, h.Exp);
        Assert.Equal(30, h.Kills);
        Assert.Equal(360 / (290 / 3600.0), h.PerHour!.Value, 1);
        Assert.Equal(300 / (100 / 3600.0), rows[1].PerHour!.Value, 1);
    }

    [Fact]
    public void Filtered_by_map_and_day()
    {
        var g = Sample();
        Assert.Equal(6, XpBrowse.Filter(g, "Ant Tunnel I", null, null).Count);
        Assert.Equal(30, XpBrowse.Filter(g, null, Day1.LocalDateTime.Date, Day1.LocalDateTime.Date).Count);
        Assert.Empty(XpBrowse.Filter(g, "Henesys Hunting Ground I", Day1.LocalDateTime.Date.AddDays(1), null));
        var days = XpBrowse.Group(g, XpGrouping.Day);
        Assert.Equal(2, days.Count);
        Assert.Equal(300, days[0].Exp); // newest day first
    }

    [Fact]
    public void Misread_map_names_file_under_the_known_name()
    {
        var names = new MapNames(["Henesys Hunting Ground I"]);
        Assert.True(names.Add("Henesys Hunting Ground l"));
        Assert.Equal("Henesys Hunting Ground I", names.Current);
        Assert.False(names.Add(null)); // unreadable: keep the last name
        Assert.True(names.Add("Ant Tunnel I"));
        Assert.Equal("Ant Tunnel I", names.Current);
    }

    [Fact]
    public void Log_round_trips_through_its_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maplelite-xplog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new XpLog(dir);
            foreach (var g in Sample()) log.Add(g);
            log.Flush();
            log.Add(new XpGain(Day1.AddDays(2), 7, "")); // not flushed yet
            var again = new XpLog(dir).All();
            Assert.Equal(36, again.Count);
            Assert.Equal(Sample()[0], again[0]);
            Assert.Equal(37, log.All().Count);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}

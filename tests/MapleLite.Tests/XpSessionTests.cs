using MapleLite.Plugins.XpTracker;
using Xunit;

namespace MapleLite.Tests;

public class XpSessionTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
    const long Need = 40_000;
    static double Pct(long exp) => Math.Round(exp * 100.0 / Need, 2);

    /// <summary>Feeds readings 200 ms apart (5 a second, as the plugin reads) and returns the gains reported.</summary>
    static List<long> Feed(XpSession s, params (long exp, double pct)[] readings)
    {
        var gains = new List<long>();
        foreach (var (exp, pct) in readings)
            gains.Add(s.Add(T0.AddMilliseconds(200 * gains.Count), exp, pct));
        return gains;
    }

    static (long, double) R(long exp) => (exp, Pct(exp));

    [Fact]
    public void KillsCountStraightAway()
    {
        var s = new XpSession();
        var gains = Feed(s, R(30_000), R(30_020), R(30_020), R(30_470));
        Assert.Equal([0, 20, 0, 450], gains);
        Assert.Equal(470, s.Gained);
        Assert.Equal(2, s.Gains);
    }

    [Fact]
    public void AGarbledReadThatDoesNotFitTheLevelIsNotCounted()
    {
        var s = new XpSession();
        // the bar is half covered: EXP reads 180,000 too high, but the percent is still the real one, for a few seconds
        var bad = (210_000L, Pct(30_000));
        var readings = new List<(long, double)> { R(30_000), R(30_000) };
        for (int i = 0; i < 10; i++) readings.Add(bad);
        readings.AddRange([R(30_000), R(30_020), R(30_020)]);
        Feed(s, [.. readings]);
        Assert.Equal(20, s.Gained);
        Assert.Equal(1, s.Gains);
        Assert.Equal(30_020, s.Exp);
    }

    [Fact]
    public void AnOutOfLineValueThatStaysIsTakenWithoutCountingIt()
    {
        var s = new XpSession();
        var other = (90_000L, 50.0); // not the same level's numbers, but it doesn't go away
        var readings = new List<(long, double)> { R(30_000), R(30_000) };
        for (int i = 0; i < 20; i++) readings.Add(other);
        readings.AddRange([(90_040L, 50.02), (90_040L, 50.02)]);
        Feed(s, [.. readings]);
        Assert.Equal(90_040, s.Exp);
        Assert.Equal(40, s.Gained);
        Assert.Equal(1, s.Gains);
    }

    [Fact]
    public void LevelUpsStillCount()
    {
        var s = new XpSession();
        Feed(s, R(39_900), R(39_900), (50, 0.05), (50, 0.05), (80, 0.08), (80, 0.08));
        Assert.Equal(1, s.Levels);
        Assert.Equal(30, s.Gained);
    }
}

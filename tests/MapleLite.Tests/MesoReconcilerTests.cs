using MapleLite.Core.Game;
using MapleLite.Plugins.Loot;
using Xunit;

namespace MapleLite.Tests;

public class MesoReconcilerTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 17, 17, 29, 0, TimeSpan.Zero);
    static DateTimeOffset At(double s) => T0.AddSeconds(s);

    /// <summary>Feeds the total once per 0.25 s from <paramref name="from"/> to <paramref name="to"/> seconds; returns what was reported.</summary>
    static long Hold(MesoReconciler m, long total, double from, double to)
    {
        long sum = 0;
        for (double s = from; s <= to; s += 0.25) sum += m.Read(total, At(s));
        return sum;
    }

    [Fact]
    public void Ten_pickups_with_six_notices_reports_the_four_hidden_ones()
    {
        var m = new MesoReconciler();
        Hold(m, 1_837_211, 0, 3);
        long reported = 0;
        for (int i = 1; i <= 10; i++)
        {
            if (i <= 6) m.Noticed(10);                       // the game shows six notices, then nothing
            reported += Hold(m, 1_837_211 + 10 * i, 3 + i * 0.3, 3 + i * 0.3 + 0.25);
        }
        reported += Hold(m, 1_837_311, 6.5, 10);
        Assert.Equal(40, reported);
    }

    [Fact]
    public void Every_pickup_noticed_reports_nothing()
    {
        var m = new MesoReconciler();
        Hold(m, 500, 0, 3);
        m.Noticed(10); m.Noticed(10);
        Assert.Equal(0, Hold(m, 520, 3, 8));
    }

    [Fact]
    public void A_rise_without_any_notice_is_not_a_pickup()
    {
        var m = new MesoReconciler();
        Hold(m, 500, 0, 3);
        Assert.Equal(0, Hold(m, 9_500, 3, 8));              // sold something to a shop
    }

    [Fact]
    public void Dropping_coins_then_picking_them_up()
    {
        var m = new MesoReconciler();
        Hold(m, 1_000, 0, 3);
        Assert.Equal(0, Hold(m, 900, 3, 7));                // dropped 100: the baseline moves down
        for (int i = 0; i < 6; i++) m.Noticed(10);
        Assert.Equal(40, Hold(m, 1_000, 7, 12));            // picked all 100 up, six notices showed
    }

    // From a recording: coins dropped and picked straight back up, so the total never settled at its low point.
    [Fact]
    public void Dropping_and_picking_up_without_a_pause()
    {
        var m = new MesoReconciler();
        Hold(m, 1_837_191, 0, 3);
        Hold(m, 1_837_181, 3, 3.5);                          // dropping
        Hold(m, 1_837_171, 3.75, 4.75);
        long reported = 0;
        for (int i = 1; i <= 10; i++)
        {
            if (i <= 6) m.Noticed(10);
            reported += Hold(m, 1_837_171 + 10 * i, 4.75 + i * 0.3, 4.75 + i * 0.3 + 0.25);
        }
        reported += Hold(m, 1_837_271, 8.5, 12);
        Assert.Equal(40, reported);
    }

    [Fact]
    public void A_single_misread_low_total_is_ignored()
    {
        var m = new MesoReconciler();
        Hold(m, 1_000, 0, 3);
        m.Read(100, At(3));                                  // one bad read
        m.Noticed(10);
        Assert.Equal(0, Hold(m, 1_010, 3.25, 7));
    }

    [Fact]
    public void Nothing_is_compared_until_the_total_settles()
    {
        var m = new MesoReconciler();
        Hold(m, 100, 0, 3);
        m.Noticed(10);
        Assert.Equal(0, Hold(m, 150, 3, 4));               // still within the settle time
        Assert.Equal(40, Hold(m, 150, 4.25, 7));
    }

    [Fact]
    public void Losing_sight_of_the_total_starts_again()
    {
        var m = new MesoReconciler();
        Hold(m, 100, 0, 3);
        m.Noticed(10);
        m.Lost();                                           // inventory closed
        Assert.Equal(0, Hold(m, 5_000, 10, 15));            // reopened later: a new baseline, nothing reported
    }

    [Theory]
    [InlineData("1,837,311", 1_837_311L)]
    [InlineData("1.837.311", 1_837_311L)]
    [InlineData("950", 950L)]
    [InlineData("0", 0L)]
    public void Meso_total_text(string text, long value) => Assert.Equal(value, MesoTotal.Parse(text));

    [Theory]
    [InlineData("1,83,311")]
    [InlineData("o 1,837,311")]
    [InlineData("MESOS")]
    [InlineData("")]
    public void Not_a_meso_total(string text) => Assert.Null(MesoTotal.Parse(text));
}

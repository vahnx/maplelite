using MapleLite.Plugins.XpTracker;
using Xunit;

namespace MapleLite.Tests;

public class RewardSpotterTests
{
    [Fact]
    public void Only_rises_well_above_recent_kills_are_checked()
    {
        var s = new RewardSpotter();
        foreach (var k in new long[] { 20, 24, 36, 20, 43, 24, 20 }) s.Killed(k);
        Assert.False(s.Worth(48));
        Assert.False(s.Worth(90));   // under the smallest reward worth checking
        Assert.True(s.Worth(450));   // a KPQ stage clear
        Assert.True(s.Worth(1200));  // a KPQ completion
    }

    [Theory]
    [InlineData("You have gained experience (+1200)", 1200, true)]
    [InlineData("You have gained experience (+1,200)", 1200, true)]
    [InlineData("You have gained experience (t450)", 450, true)]   // + read as t
    [InlineData("You received EXP (+450)", 450, true)]
    [InlineData("[14:26] Tazlil : selling 1200 for 1m", 1200, false)] // a player's number isn't EXP
    [InlineData("You have gained experience (+12000)", 1200, false)]
    [InlineData("You have gained experience (+450)", 1200, false)]
    public void Finds_the_amount_as_EXP_in_chat(string chat, long amount, bool found) =>
        Assert.Equal(found, RewardSpotter.InChat("[14:25] someone : hi\n" + chat, amount));

    [Fact]
    public void Kills_and_rewards_are_kept_apart_in_the_log_and_summaries()
    {
        var dir = Directory.CreateTempSubdirectory("xp-reward").FullName;
        try
        {
            var t = DateTimeOffset.Now;
            var log = new XpLog(dir);
            log.Add(new XpGain(t, 20, "KPQ"));
            log.Add(new XpGain(t.AddSeconds(5), 40, "KPQ"));
            log.Add(new XpGain(t.AddSeconds(10), 450, "KPQ", Reward: true));
            log.Flush();
            var all = new XpLog(dir).All();
            Assert.Equal([false, false, true], all.Select(g => g.Reward));

            var total = XpBrowse.Total([.. all]);
            Assert.Equal((510L, 2, 1, 450L), (total.Exp, total.Kills, total.Rewards, total.RewardExp));
            Assert.Equal(30, total.AveragePerKill);
            Assert.Equal(60, XpBrowse.OfKind(all, XpKind.Kills).Sum(g => g.Amount));
            Assert.Equal(450, XpBrowse.OfKind(all, XpKind.Rewards).Sum(g => g.Amount));
        }
        finally { Directory.Delete(dir, true); }
    }
}

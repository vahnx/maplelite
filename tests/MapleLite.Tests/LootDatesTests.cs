using MapleLite.Plugins.Loot;
using Xunit;

namespace MapleLite.Tests;

public class LootDatesTests
{
    static readonly DateTime Today = new(2026, 11, 18);
    static readonly DateTimeOffset SessionStart = new(new DateTime(2026, 11, 17, 21, 30, 0));

    [Fact]
    public void Presets_fill_the_date_pickers()
    {
        Assert.Equal((Today, Today), LootView.PresetDays(1, Today, SessionStart));
        Assert.Equal((Today.AddDays(-1), Today.AddDays(-1)), LootView.PresetDays(2, Today, SessionStart));
        Assert.Equal((Today.AddDays(-6), Today), LootView.PresetDays(3, Today, SessionStart));
        Assert.Equal((Today.AddDays(-29), Today), LootView.PresetDays(4, Today, SessionStart));
        Assert.Equal(((DateTime?)null, (DateTime?)null), LootView.PresetDays(5, Today, SessionStart));
        Assert.Equal((new DateTime(2026, 11, 17), Today), LootView.PresetDays(0, Today, SessionStart));
    }

    [Fact]
    public void Picked_days_count_from_the_first_midnight_to_the_end_of_the_last_day()
    {
        var (from, to) = LootView.Window(6, SessionStart, new DateTime(2026, 11, 10), new DateTime(2026, 11, 12));
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 10)), from);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 13)), to);
    }

    [Fact]
    public void An_empty_day_leaves_that_end_open()
    {
        var (from, to) = LootView.Window(6, SessionStart, null, new DateTime(2026, 11, 12));
        Assert.Equal(DateTimeOffset.MinValue, from);
        (from, to) = LootView.Window(6, SessionStart, new DateTime(2026, 11, 12), null);
        Assert.Equal(DateTimeOffset.MaxValue, to);
    }

    [Fact]
    public void This_session_starts_when_MapleLite_did() =>
        Assert.Equal(SessionStart, LootView.Window(0, SessionStart, SessionStart.Date, Today).From);
}

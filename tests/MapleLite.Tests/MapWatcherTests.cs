using System.Text.Json;
using MapleLite.Core.Game;
using MapleLite.Plugins.Loot;
using Xunit;

namespace MapleLite.Tests;

public class MapWatcherTests
{
    [Theory]
    [InlineData("Free Market <1>", "Free Market")]
    [InlineData("Victoria Road - Kerning City Self-Defence Item Store", "Victoria Road - Kerning City Self-Defence Item Store")]
    [InlineData("Hidden Street - Abandoned Tower <3>", "Hidden Street - Abandoned Tower")]
    [InlineData("", null)]
    [InlineData(" - ", null)]
    public void Room_numbers_are_dropped(string read, string? expected) => Assert.Equal(expected, MapWatcher.Clean(read));

    static MapWatcher New() => new(null!, null!);

    [Fact]
    public void A_name_is_taken_once_it_reads_the_same_twice()
    {
        var m = New();
        Assert.Null(m.Take("Henesys"));
        Assert.Equal("Henesys", m.Take("Henesys"));
    }

    [Fact]
    public void One_odd_read_does_not_change_the_map()
    {
        var m = New();
        m.Take("Henesys"); m.Take("Henesys");
        Assert.Equal("Henesys", m.Take("Hencsys"));
        Assert.Equal("Henesys", m.Take("Henesys"));
        Assert.Equal("Henesys", m.Take("Kerning City"));
        Assert.Equal("Kerning City", m.Take("Kerning City"));
    }

    [Fact]
    public void An_unreadable_minimap_keeps_the_last_map()
    {
        var m = New();
        m.Take("Henesys"); m.Take("Henesys");
        Assert.Equal("Henesys", m.Take(null));
    }

    [Fact]
    public void The_map_is_saved_with_a_pickup_and_older_records_still_read()
    {
        var r = LootRecord.From(DateTimeOffset.Now, new LootDrop("Item", "Coupon", 1), "Coupon x1 earned. (Etc)", "OCR · notice", "Kerning City");
        var back = JsonSerializer.Deserialize<LootRecord>(JsonSerializer.Serialize(r))!;
        Assert.Equal("Kerning City", back.Map);

        var old = JsonSerializer.Deserialize<LootRecord>("""{"Time":"2026-11-17T21:20:39-05:00","Kind":"Item","Name":"Coupon","Quantity":1,"Text":"x","Source":"chat"}""")!;
        Assert.Null(old.Map);
        Assert.DoesNotContain("Map", JsonSerializer.Serialize(old));
    }
}

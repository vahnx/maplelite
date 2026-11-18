using MapleLite.Plugins.Loot;
using Xunit;

namespace MapleLite.Tests;

public class NoticeTrackerTests
{
    const double Row = 20, Bottom = 400;
    const string Ten = "You have gained mesos (+10)", Fifty = "You have gained mesos (+50)",
        Liquid = "You have gained an item in the Etc tab (Squishy Liquid)";

    /// <summary>Rows listed top to bottom; the last sits at the bottom slot, like the game's stack.</summary>
    static List<NoticeLine> Stack(params string[] texts) =>
        texts.Select((t, i) => new NoticeLine(t, Bottom - (texts.Length - 1 - i) * Row, 16, 230)).ToList();

    [Fact]
    public void Identical_pickups_each_count()
    {
        var t = new NoticeTracker();
        Assert.Single(t.Update(Stack(Ten)));
        Assert.Empty(t.Update(Stack(Ten)));                 // same notice, still showing
        Assert.Single(t.Update(Stack(Ten, Ten)));           // a second +10 pushed the first up
        Assert.Empty(t.Update(Stack(Ten, Ten)));
        Assert.Single(t.Update(Stack(Ten, Ten, Ten)));
    }

    [Fact]
    public void Two_identical_items_picked_up_between_reads()
    {
        var t = new NoticeTracker();
        Assert.Equal(2, t.Update(Stack(Liquid, Liquid)).Count);
        Assert.Empty(t.Update(Stack(Liquid, Liquid)));
    }

    [Fact]
    public void Different_amounts_in_the_same_slot_after_a_shift()
    {
        var t = new NoticeTracker();
        t.Update(Stack(Ten));
        var fresh = t.Update(Stack(Ten, Fifty));
        Assert.Equal(Fifty, Assert.Single(fresh).Text);
    }

    [Fact]
    public void Oldest_fading_out_as_a_new_one_arrives()
    {
        var t = new NoticeTracker();
        t.Update([new NoticeLine(Ten, Bottom - Row, 16, 120), new NoticeLine(Ten, Bottom, 16, 200)]);
        // The dim top one vanished and a new one arrived: still two rows, but the old bottom one moved up.
        Assert.Single(t.Update([new NoticeLine(Ten, Bottom - Row, 16, 195), new NoticeLine(Ten, Bottom, 16, 235)]));
    }

    [Fact]
    public void A_fresh_notice_replacing_a_faded_one_in_place()
    {
        var t = new NoticeTracker();
        t.Update([new NoticeLine(Ten, Bottom, 16, 120)]);   // dim: about to vanish
        Assert.Single(t.Update([new NoticeLine(Ten, Bottom, 16, 235)])); // bright again: a new +10
    }

    [Fact]
    public void One_missed_read_is_not_a_new_pickup()
    {
        var t = new NoticeTracker();
        t.Update(Stack(Ten));
        Assert.Empty(t.Update([]));                          // OCR missed it once
        Assert.Empty(t.Update(Stack(Ten)));
        Assert.Empty(t.Update([])); Assert.Empty(t.Update([])); // gone
        Assert.Single(t.Update(Stack(Ten)));                 // a new one later counts
    }

    // Real reads of the "x1 earned. (Etc)" notices from a recording, over a light stone floor.
    [Theory]
    [InlineData("Slime Bubble x1 earned. (Etc)", "Slime Bubble", 1)]
    [InlineData("Slime 3uOOIe:-1 earned. (Etc)", "Slime Bubble", 1)]
    [InlineData("Lugin•s Banana ZI earned. (Etc)", "Lupin's Banana", 1)]
    [InlineData("Squishy Liquid x12 earned. (Etc)", "Squishy Liquid", 12)]
    [InlineData("Lupin Doll x1 eamed. (Etc)", "Lupin Doll", 1)]
    // Over grass: the end of the name lost, or run together.
    [InlineData("-Jr. Necä earned. (Eir-", "Jr Necki Skin", 1)]
    [InlineData("Jr. NeckfSkin x1 earned. (Etc)", "Jr Necki Skin", 1)]
    public void Earned_notices_with_catalog_names(string read, string name, long qty)
    {
        var names = new ItemNames(["Slime Bubble", "Lupin's Banana", "Lupin Doll", "Squishy Liquid", "Squishy Shoes", "Snail Shell",
            "Jr Necki Skin", "Jr. Sentinel Shellpiece", "Jr. Boogie's Horn", "Necki Flower"]);
        var drop = MapleLite.Core.Game.GameText.Pickup(read);
        Assert.NotNull(drop);
        Assert.Equal(("Item", name, qty), (drop!.Kind, names.Fix(drop).Name, drop.Quantity));
    }

    // Real dark-strokes read of "You have gained mesos (+10)" over a light stone floor: g read as q.
    [Fact]
    public void Meso_notice_read_from_its_outline()
    {
        var drop = MapleLite.Core.Game.GameText.Pickup("---{ou tlå•te qained mesos (+10)");
        Assert.NotNull(drop);
        Assert.Equal(("Mesos", 10L), (drop!.Kind, drop.Quantity));
    }

    [Fact]
    public void Earned_wording_does_not_catch_other_lines()
    {
        Assert.Null(MapleLite.Core.Game.GameText.Pickup("You have earned 500 experience"));
        Assert.Null(MapleLite.Core.Game.GameText.Pickup("Lupin Doll"));
    }

    [Fact]
    public void Differently_garbled_reads_of_one_item_notice_are_one_pickup()
    {
        var t = new NoticeTracker();
        Assert.Single(t.Update([new NoticeLine("Item|Lugin•s Banana|1", Bottom, 16, 230)]));
        Assert.Empty(t.Update([new NoticeLine("Item|Lugirt•; Banana A.|1", Bottom, 16, 228)]));
    }

    [Fact]
    public void Ocr_noise_on_the_same_notice_is_not_new()
    {
        var t = new NoticeTracker();
        t.Update(Stack(Ten));
        Assert.Empty(t.Update(Stack("You have gained rnesos (+10)")));
    }
}

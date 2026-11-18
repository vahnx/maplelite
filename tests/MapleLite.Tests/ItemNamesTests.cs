using MapleLite.Plugins.Loot;
using Xunit;

namespace MapleLite.Tests;

// From a night of Kerning party quests: what the pickup notices were read as, and what should be logged.
public class ItemNamesTests
{
    static readonly ItemNames Names = new(["Cass", "Coupon", "Squishy Liquid", "Jr Necki Skin", "Lupin's Banana", "Red Potion"]);

    [Fact]
    public void A_short_name_one_letter_off_is_a_different_item() => Assert.Null(Names.Match("Pass"));

    [Theory]
    [InlineData("Coupon", "Coupon")]
    [InlineData("COUgon", "Coupon")]
    [InlineData("epupon", "Coupon")]
    [InlineData("Lugin•s Banana", "Lupin's Banana")]
    [InlineData("Cass", "Cass")]
    public void Close_reads_are_corrected(string read, string expected) => Assert.Equal(expected, Names.Match(read));

    [Theory]
    [InlineData("Pass")]
    [InlineData("Shoes Speed Scroll: Greater")]
    [InlineData("Jr. Necki Skin")]
    [InlineData("Lupin's Banana")]
    public void Clean_names_the_catalog_lacks_are_kept(string name) => Assert.True(Names.Trusted(name, known: false));

    [Theory]
    [InlineData("C@tJpbh")]
    [InlineData("iC gon")]
    [InlineData("tr.:-ll")]
    [InlineData("C n")]
    [InlineData("RI")]
    [InlineData("CåØ€n")]
    [InlineData("CoiJß13h")]
    [InlineData("ouoonz-t")]
    [InlineData("$0!J99n")]
    [InlineData("C\"üpon x1")]
    public void Garbled_reads_are_left_out(string name) => Assert.False(Names.Trusted(name, Names.Match(name) != null));

    [Fact]
    public void A_count_left_in_the_name_is_left_out_even_when_the_name_is_known() =>
        Assert.False(Names.Trusted("Coupon x1", known: true));
}

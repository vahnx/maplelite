using MapleLite.Core.Game;
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

    [Fact]
    public void ReopeningTheLootJournalStacksClearOcrVariantsUnderTheCatalogName()
    {
        using var folder = new TempFolder();
        var catalog = ItemCatalog.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "..", "src", "MapleLite.Core", "GameData", "Items")));
        var names = new ItemNames(catalog);
        var original = new LootLog(folder.Path);
        var now = DateTimeOffset.Now;
        LootRecord Row(string name, long quantity = 1) => new(now, "Item", name, quantity, $"{name} earned.", "OCR · notice") { Map = "Henesys" };

        original.Add(Row("Fie Flour:|", 3));
        original.Add(Row("Pie Flour:l", 2));
        original.Add(Row("Pie Flour", 4));
        original.Add(Row("Slue Pojon:|"));
        original.Add(Row("[20: 121 Blue Potion x2Cl", 8));
        original.Add(Row("Blue Potion", 2));
        original.Add(Row("Return Scroll to"));
        original.Add(Row("Green Primros") with { Text = "Green Primros... x1 earned. (Etc)" });
        original.Add(Row("Cornnanionls Maaic Box"));
        var journal = Assert.Single(Directory.GetFiles(folder.Path));
        var raw = File.ReadAllText(journal);

        Assert.Equal("Blue Potion", names.Match("Slue Pojon:|"));
        Assert.Equal("Pie Flour", names.Match("Fie Flour:|"));
        var reopened = new LootLog(folder.Path, names);
        var totals = LootView.Totals(reopened.Records);
        Assert.Contains(totals, row => row.Name == "Pie Flour" && row.Quantity == 9 && row.Pickups == 3);
        Assert.Contains(totals, row => row.Name == "Blue Potion" && row.Quantity == 11 && row.Pickups == 3);
        Assert.Contains(totals, row => row.Name == "Return Scroll to" && row.Quantity == 1);
        Assert.Contains(totals, row => row.Name == "Green Primrose Seed" && row.Quantity == 1);
        Assert.Contains(totals, row => row.Name == "Companion's Magic Box" && row.Quantity == 1);
        Assert.Equal(Row("Fie Flour:|", 3) with { Name = "Pie Flour" }, reopened.Records.Single(r => r.Text == "Fie Flour:| earned."));
        Assert.NotNull(names.IconPath("Pie Flour"));
        Assert.NotNull(names.IconPath("Blue Potion"));
        Assert.Equal(raw, File.ReadAllText(journal));

        var appended = reopened.Append(Row("Slue Pojon:l", 2));
        Assert.Equal("Blue Potion", appended.Name);
        Assert.Contains(LootView.Totals(new LootLog(folder.Path, names).Records),
            row => row.Name == "Blue Potion" && row.Quantity == 13 && row.Pickups == 4);
    }

    [Fact]
    public void HistoryLeavesAmbiguousOrUncataloguedItemsAlone()
    {
        var names = new ItemNames(["Red Potion", "Blue Potion", "White Potion", "Blue Motion", "Cass"]);
        Assert.Null(names.Match("Blue Sotion", allowPartial: false));
        Assert.Null(names.Match("Potion", allowPartial: false));
        Assert.Null(names.Match("Pass", allowPartial: false));
    }

    static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;

    sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("maplelite-loot-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

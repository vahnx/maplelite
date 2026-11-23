using System.Text.Json;
using Avalonia.Media;
using MapleLite.Core.Game;
using MapleLite.Plugins.Loot;
using Xunit;

namespace MapleLite.Tests;

public class LootHighlightsTests
{
    [Fact]
    public void Older_settings_default_to_five_inactive_ranges()
    {
        var settings = JsonSerializer.Deserialize<LootSettings>("""{"ImportChecked":true}""")!;
        Assert.True(settings.ImportChecked);
        Assert.False(settings.ShowDropOverlay);
        Assert.Empty(settings.ItemHighlights);
        Assert.Equal(5, settings.PriceRanges.Count);
        Assert.All(settings.PriceRanges, range => Assert.False(range.Matches(1)));
    }

    [Fact]
    public void Price_limits_are_inclusive_and_the_first_overlap_wins()
    {
        var catalog = new ItemCatalog(
        [
            new() { id = 1, name = "Scroll" }, new() { id = 2, name = "Potion" },
            new() { id = 3, name = "Ore" }, new() { id = 4, name = "Crystal" },
            new() { id = 5, name = "Gem" }, new() { id = 6, name = "Gem" },
        ], null);
        var highlights = new LootHighlights(catalog);
        highlights.SetRules([new(100, 200, "#FF8800"), new(null, 100, "#22AAFF"), new(201, null, "#44CC88")], []);
        highlights.SetPrices(new Dictionary<int, long> { [1] = 100, [2] = 99, [3] = 200, [5] = 100, [6] = 500 });
        Assert.Equal(0xFF8800u, highlights.Find("Item", "scroll")?.Rgb);
        Assert.Equal(Color.Parse("#FF8800"), highlights.Find("Item", "Ore")?.Color);
        Assert.Equal(0x22AAFFu, highlights.Find("Item", "Potion")?.Rgb);
        Assert.Null(highlights.Find("Item", "Crystal"));
        Assert.Null(highlights.Find("Item", "Gem"));
        Assert.Null(highlights.Find("Mesos", "Scroll"));
        Assert.Null(highlights.Find("Item", "Unknown"));

        var revision = highlights.Revision;
        Assert.True(highlights.SetPrices(new Dictionary<int, long> { [1] = 201 }));
        Assert.True(highlights.Revision > revision);
        Assert.Equal(0x44CC88u, highlights.Find("Item", "Scroll")?.Rgb);
        Assert.False(highlights.SetPrices(new Dictionary<int, long> { [1] = 201 }));
    }

    [Fact]
    public void Specific_items_work_without_prices_and_override_range_colors()
    {
        var catalog = new ItemCatalog([new() { id = 1, name = "Scroll" }], null);
        var highlights = new LootHighlights(catalog);
        var settings = new LootSettings { ItemHighlights = [new("Scroll", "#00FFAA"), new("Quest Gem", "#FFAA00")] };
        settings = JsonSerializer.Deserialize<LootSettings>(JsonSerializer.Serialize(settings))!;
        highlights.SetRules(settings.PriceRanges, settings.ItemHighlights);
        Assert.False(highlights.HasPriceRanges);
        Assert.Equal(0xFFAA00u, highlights.Find("Item", "quest gem")?.Rgb);
        Assert.Equal("Quest Gem ×1", highlights.Find("Item", "Quest Gem")?.WithPrice("Quest Gem ×1"));
        Assert.Null(highlights.Find("Mesos", "Quest Gem"));

        LootPriceRange[] ranges = [new(100, 200, "#FF8800")];
        highlights.SetPrices(new Dictionary<int, long> { [1] = 150 });
        highlights.SetRules(ranges, settings.ItemHighlights);
        Assert.Equal(0x00FFAAu, highlights.Find("Item", "Scroll")?.Rgb);
        Assert.Equal(150L, highlights.Find("Item", "Scroll")?.UnitPrice);

        highlights.SetRules(ranges, []);
        Assert.Equal(0xFF8800u, highlights.Find("Item", "Scroll")?.Rgb);
        Assert.Null(highlights.Find("Item", "Quest Gem"));
    }
}

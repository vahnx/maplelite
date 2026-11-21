using MapleLite.Core.Market;
using MapleLite.Plugins.SaleCard;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MapleLite.Tests;

public class MarketAndCsvTests
{
    [Fact]
    public void Csv_roundtrips_quotes_multiline_and_formula_like_text()
    {
        var item = new SaleItem { Key = "test", ItemId = 123, Name = "=SUM(1,2)", Qty = "20 ×3",
            Custom = "Offer", Stats = "STR +3\n\"Rare\", good", Slots = "2", Buying = true, Include = false, PricePercent = 85, Stacks = 3 };
        var csv = CardCsv.Export([item]);
        Assert.Contains("'=SUM", csv);
        var row = Assert.Single(CardCsv.Parse(csv));
        Assert.Equal(item.Name, row.Name); Assert.Equal(item.Stats, row.Stats); Assert.Equal(item.Qty, row.Qty);
        Assert.True(row.Buying); Assert.False(row.Include); Assert.Equal(85, row.PricePercent); Assert.Equal(3, row.Stacks);
    }

    [Fact]
    public void Csv_rejects_duplicate_keys_and_invalid_rows()
    {
        var item = new SaleItem { Key = "same", Name = "Item" };
        Assert.Throws<FormatException>(() => CardCsv.Parse(CardCsv.Export([item, item])));
        Assert.Throws<FormatException>(() => CardCsv.Parse("Name,Price\nItem,100"));
        Assert.Throws<FormatException>(() => CardCsv.Parse(CardCsv.Export([item]).Replace("\"100\"", "\"500\"")));
    }

    [Fact]
    public void Database_reopens_deduplicates_and_preserves_sources_and_servers()
    {
        string folder = Path.Combine(Path.GetTempPath(), "MapleLite-test-" + Guid.NewGuid());
        string path = Path.Combine(folder, "prices.sqlite");
        try
        {
            var first = new MarketObservation(123, "FM", "Windia", DateTime.UtcNow, 500, 2, "seller", "STR +3", "1");
            new MarketDatabase(path).Append([first, first, first with { Source = "website", Typical = 600 }, first with { Server = "other" }]);
            var rows = new MarketDatabase(path).Recent(123);
            Assert.Equal(3, rows.Count); Assert.Contains(first, rows);
            Assert.Empty(new MarketDatabase(path).Recent(456));
            Assert.Throws<ArgumentException>(() => new MarketDatabase(path).Append([first with { ItemId = 456 }, first with { ItemId = -1 }]));
            Assert.Empty(new MarketDatabase(path).Recent(456)); // whole invalid batch rolls back
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

using MapleLite.Core.Game;
using Xunit;

namespace MapleLite.Tests;

public class ItemCatalogTests
{
    [Fact]
    public void LoadsItemsAndFindsIconsByIdAndName()
    {
        var dir = Directory.CreateTempSubdirectory("maplelite-catalog-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "items.json"),
                """[{"id":4000000,"slug":"snail-shell","name":"Snail Shell"},{"id":4000001,"slug":"orange-mushroom-cap","name":"Orange Mushroom Cap"},{"id":4000000,"slug":"dup","name":"Duplicate"}]""");
            Directory.CreateDirectory(Path.Combine(dir, "icons"));
            File.WriteAllBytes(Path.Combine(dir, "icons", "4000000.png"), [1]);

            var c = ItemCatalog.Load(dir);

            Assert.Equal(2, c.Count);                                   // a repeated id is kept once
            Assert.Equal("Snail Shell", c.Name(4000000));
            Assert.Equal(4000001, c.ByName("orange mushroom cap")?.id);  // names ignore case
            Assert.EndsWith("4000000.png", c.IconPath("Snail Shell"));
            Assert.Null(c.IconPath(4000001));                           // in the catalog but no picture
            Assert.Null(c.IconPath("Not An Item"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MissingFolderGivesAnEmptyCatalog()
    {
        var c = ItemCatalog.Load(Path.Combine(Path.GetTempPath(), "maplelite-no-such-folder-" + Guid.NewGuid()));
        Assert.Equal(0, c.Count);
        Assert.Null(c.IconPath(4000000));
    }

    [Fact]
    public void TheShippedCatalogIsInCore()
    {
        var c = ItemCatalog.Load(Path.Combine(RepoRoot(), "src", "MapleLite.Core", "GameData", "Items"));
        Assert.True(c.Count > 1000, $"only {c.Count} items");
        Assert.NotNull(c.IconFolder);
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}

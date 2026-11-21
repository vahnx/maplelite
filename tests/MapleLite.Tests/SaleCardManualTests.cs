using System.Reflection;
using MapleLite.Core.Game;
using MapleLite.Plugins.SaleCard;
using MapleLite.Sdk;
using Xunit;

namespace MapleLite.Tests;

public class SaleCardManualTests
{
    static readonly CatalogItem Ore = new() { id = 4000000, name = "Ore" };
    static readonly CatalogItem Gem = new() { id = 4000001, name = "Gem" };
    static readonly CatalogItem Scroll = new() { id = 2040000, name = "Scroll" };

    public class ContextProxy : DispatchProxy
    {
        public IPluginStorage Storage = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == "get_Storage" ? Storage : throw new NotSupportedException(method.Name);
    }
    sealed class Storage(string folder) : IPluginStorage
    {
        public string Folder => folder;
        public T Load<T>(string name) where T : new() => new();
        public void Save<T>(string name, T value) { }
    }
    static SaleCardEngine Engine(string? folder = null)
    {
        var ctx = DispatchProxy.Create<IPluginContext, ContextProxy>();
        ((ContextProxy)(object)ctx).Storage = new Storage(folder ?? Path.Combine(Path.GetTempPath(), "MapleLite-manual-tests", Guid.NewGuid().ToString("N")));
        var e = new SaleCardEngine(ctx) { Vision = new Vision(new ItemCatalog([Ore, Gem, Scroll], null)) };
        e.LoadSession();
        return e;
    }
    [Fact]
    public void Manual_order_keeps_individual_stacks_and_survives_restart()
    {
        var e = Engine(); var a = e.AddByHand(Ore); var b = e.AddByHand(Ore); var c = e.AddByHand(Gem);
        a.Qty = "10"; b.Qty = "20";
        e.MoveCardItem(c, -1);
        Assert.Equal(new[] { a.Key, c.Key, b.Key }, e.Card().items.Select(i => i.Key));
        e.SetBuying(Scroll, true);
        var before = e.Card().items.Select(i => i.Key).ToList();
        e.MoveCardItem(e.BuyCard(Scroll.id)!, -1);
        Assert.Equal(before, e.Card().items.Select(i => i.Key));
        e.SaveSession(); var reopened = Engine(e.DataFolder);
        Assert.Equal(before, reopened.Card().items.Select(i => i.Key));
        Assert.Equal("10", reopened.Items.Single(i => i.Key == a.Key).Qty);
        Assert.False(reopened.Card().options.Pack);
    }

    [Fact]
    public void Selected_filters_match_without_changing_card_membership()
    {
        var item = new SaleItem { Include = true, Name = "Emerald Ore", Category = "Ores", Tab = SaleCardEngine.StorageTab };
        Assert.True(CardSelection.Selected(item, false, "ore emerald", "Storage", "Ores"));
        Assert.False(CardSelection.Selected(item, false, "", "Manual", "All types"));
        Assert.False(CardSelection.Selected(item, false, "Meba", "All sources", "All types"));
        Assert.False(CardSelection.Selected(item, true, "", null, "All types"));
        Assert.True(item.Include);
    }

    [Fact]
    public void Manual_items_and_custom_names_survive_restart_and_can_be_removed()
    {
        var e = Engine();
        var item = e.AddByHand(Ore);
        item.Qty = "12"; item.Custom = "200";
        e.AddByHand(null, "  My custom item  ");
        e.SaveSession();
        var reopened = Engine(e.DataFolder);
        Assert.Equal(2, reopened.Items.Count);
        var saved = reopened.Items.Single(i => i.ItemId == Ore.id);
        Assert.True(saved.IsAdded && saved.Picked && saved.Include);
        Assert.Equal("12", saved.Qty);
        Assert.Equal("200", saved.Custom);
        Assert.Contains(reopened.Items, i => i.Name == "My custom item");
        reopened.RemoveAdded(saved);
        Assert.Single(Engine(e.DataFolder).Items);
    }
    [Fact]
    public void Empty_custom_name_is_rejected()
    {
        var e = Engine();
        Assert.Throws<ArgumentException>(() => e.AddByHand(null, "  "));
        Assert.Empty(e.Items);
    }
    [Fact]
    public void Csv_import_is_repeatable_preserves_existing_items_and_survives_restart()
    {
        var engine = Engine();
        var original = engine.AddByHand(Ore);
        var csv = CardCsv.Export([new SaleItem { Key = "external", ItemId = Gem.id, Name = "Gem", Qty = "7", Custom = "123", Stats = "STR +2" }]);
        engine.ImportCsv(csv);
        engine.ImportCsv(csv);
        Assert.Equal(2, engine.Items.Count);
        Assert.Contains(original, engine.Items);
        var reopened = Engine(engine.DataFolder);
        var imported = reopened.Items.Single(i => i.ItemId == Gem.id);
        Assert.True(imported.IsAdded); Assert.Equal("7", imported.Qty); Assert.Equal("123", imported.Custom);
        Assert.Throws<FormatException>(() => reopened.ImportCsv(csv + "invalid,row"));
        Assert.Equal(2, reopened.Items.Count);
    }
    [Theory]
    [InlineData(4000000)]
    [InlineData(2040000)]
    public void Changed_shop_item_survives_rescan_restart_and_sold_detection(int originalId)
    {
        var e = Engine();
        var read = new ShopRead { Seller = "Test", IsOwnerView = true, Rows = [new(originalId, "Original", 2, 100, 1, "")] };
        e.S.ShopOpen = true;
        e.ImportOwnShop(read);
        var card = Assert.Single(e.Items);
        var key = card.Key;
        e.ChangeItem(card, Gem);
        Assert.Equal(key, card.Key);
        Assert.True(card.InShop);
        Assert.Equal(0, e.ImportOwnShop(read));
        Assert.Equal(Gem.id, Assert.Single(e.Items).ItemId);
        e = Engine(e.DataFolder);
        Assert.Equal(0, e.ImportOwnShop(read));
        Assert.Equal(Gem.id, Assert.Single(e.Items).ItemId);
        e.ImportOwnShop(new ShopRead { Seller = "Test", IsOwnerView = true, Sold = [new([originalId], "Original", 2, 100, 0)] });
        Assert.Empty(e.Items);
    }
    [Fact]
    public void Changing_manual_item_keeps_quantity_price_and_unique_identity()
    {
        var e = Engine();
        var a = e.AddByHand(Ore);
        var b = e.AddByHand(Ore);
        a.Qty = "7"; a.Custom = "123";
        var key = a.Key;
        e.ChangeItem(a, Gem);
        Assert.Equal(key, a.Key);
        Assert.NotEqual(b.Key, a.Key);
        Assert.Equal("7", a.Qty);
        Assert.Equal("123", a.Custom);
        Assert.Empty(e.S.Renames);
        Assert.True(SaleCardEngine.InTab(a, SaleCardEngine.AddedTab));
        Assert.False(SaleCardEngine.InTab(a, SaleCardEngine.BuyTab));
    }
    [Fact]
    public void Shop_sync_waits_for_add_and_preserves_selection_on_later_reads()
    {
        var e = Engine();
        var read = new ShopRead { Seller = "Test", IsOwnerView = true, Rows = [new(Ore.id, Ore.name, 2, 100, 1, "")] };
        e.ImportOwnShop(read);
        var card = Assert.Single(e.Items);
        Assert.False(card.Include);
        e.SetIncluded([card], true);
        e.ImportOwnShop(read);
        Assert.True(Assert.Single(e.Items).Include);
        e.SetIncluded([card], false);
        e.ImportOwnShop(read);
        Assert.False(Assert.Single(e.Items).Include);
    }

    [Fact]
    public void Available_filters_keep_manual_source_distinct_and_exclude_selected_and_buying()
    {
        var manual = new SaleItem { Name = "Earring DEX Scroll", Tab = SaleCardEngine.AddedTab, Category = "Upgrade scrolls", Include = false };
        Assert.True(CardSelection.Available(manual, "dex earring", "Manual", "Upgrade scrolls"));
        Assert.False(CardSelection.Available(manual, "", "Inventory", "All types"));
        manual.Include = true;
        Assert.False(CardSelection.Available(manual, "", "All sources", "All types"));
        manual.Include = false; manual.Tab = SaleCardEngine.BuyTab;
        Assert.False(CardSelection.Available(manual, "", "All sources", "All types"));
    }

    [Fact]
    public void Explicit_sync_selects_visible_items_without_touching_manual_unseen_or_buying()
    {
        var e = Engine();
        SaleItem Scanned(string key) { var i = new SaleItem { Key = key, ItemId = Ore.id, Tab = "Etc", Name = "Ore", Include = false }; e.Items.Add(i); return i; }
        var a = Scanned("Etc:a"); var b = Scanned("Etc:b"); var unseen = Scanned("Etc:unseen");
        var manual = e.AddByHand(Gem); manual.Include = false;
        e.SetBuying(Scroll, true);
        e.S.HideSelling = true;
        Assert.Equal(2, e.IncludeSyncedItems([a.Key, b.Key, manual.Key, "Buy:" + Scroll.id]));
        Assert.True(a.Include && b.Include); Assert.False(unseen.Include || manual.Include);
        Assert.False(e.S.HideSelling); Assert.True(e.BuyCard(Scroll.id)!.Include);
    }

    [Fact]
    public void Identical_equipment_in_distinct_slots_survives_restart_and_old_tab_filter()
    {
        var e = Engine();
        for (int col = 0; col < 3; col++) e.Items.Add(new SaleItem
        {
            Key = $"Equip:1:{col}:1032009", ItemId = 1032009, Name = "Sapphire Earrings", Tab = "Equip",
            SlotRow = 1, SlotCol = col, Include = true, Stats = "Magic Def: +30", Slots = "5"
        });
        e.S.Tab = "Use"; e.SaveSession();
        var reopened = Engine(e.DataFolder);
        Assert.Equal(3, reopened.Items.Count);
        var (cards, _) = reopened.Card();
        Assert.Equal(3, cards.Count);
    }

    [Fact]
    public void Missing_stats_requires_equipment_rolls_and_slots_but_not_buying_requirements()
    {
        var item = new SaleItem { ItemId = 1032009, Tab = "Equip" };
        Assert.True(item.MissingStats);
        item.Stats = "Magic Def: +30"; Assert.True(item.MissingStats);
        item.Slots = "0"; Assert.False(item.MissingStats);
        item.Slots = ""; item.Tab = SaleCardEngine.BuyTab; Assert.False(item.MissingStats);
        item.Tab = "Use"; item.ItemId = 2040000; item.Stats = ""; Assert.True(item.MissingStats);
        item.Stats = "Success rate: 60%"; Assert.False(item.MissingStats);
    }

}

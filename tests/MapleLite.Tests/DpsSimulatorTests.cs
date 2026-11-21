using MapleLite.Plugins.DpsSimulator;
using MapleLite.Core.Game;
using MapleLite.Core.Market;
using Xunit;

namespace MapleLite.Tests;

public class DpsSimulatorTests
{
    [Fact]
    public void Market_archive_distinguishes_manual_asks_and_exact_fm_rolls()
    {
        var db = new MarketDatabase(Path.Combine(Path.GetTempPath(), "MapleLite-tests", Guid.NewGuid().ToString("N"), "market.sqlite"));
        var gear = new PlannedGear("Weapon", 1302000, "Weapon Attack: +47", "7", false);
        db.Append([new(gear.ItemId, "FM", "Windia", DateTime.UtcNow.AddMinutes(-5), 1000, Stats: gear.Stats, Slots: "7"),
            new(gear.ItemId, "FM", "Windia", DateTime.UtcNow, 3000, Stats: "Weapon Attack: +60", Slots: "1")]);
        Assert.Equal(1000, ArchivedPrices.Find(db, gear)?.Mesos);
        db.ReplaceAskingPrices([new(gear.ItemId, EquipmentReader.Profile(gear.Stats, "7"), "2.5k")]);
        Assert.Equal(2500, ArchivedPrices.Find(db, gear)?.Mesos);
        db.ReplaceAskingPrices([]);
        Assert.Equal(1000, ArchivedPrices.Find(db, gear)?.Mesos);
        Assert.Equal(2, db.Recent(gear.ItemId).Count);
    }
    [Fact]
    public void Gear_and_target_defense_change_damage_using_classic_formula()
    {
        var settings = new CombatSettings(Level: 30, Job: Job.Warrior, Str: 132, Dex: 30, CycleMs: 1000);
        Assert.True(GearStats.TryParse("Weapon Attack: +47 (+2)\nDEX: +2", out var gear));
        var target = new Monster("test", "Test", 1, 2000, 0, 100, 0);
        var original = Combat.Estimate(settings, gear, 1302000, target, false);
        Assert.Equal(1, original.HitChance);
        Assert.InRange(original.Dps, 117, 119);
        var upgraded = Combat.Estimate(settings with { PreviewCycleMs = 500 }, gear, 1302000, target, true);
        Assert.Equal(original.Dps * 2, upgraded.Dps, 6);
        var defended = Combat.Estimate(settings, gear, 1302000, target with { PhysicalDefense = 100 }, false);
        Assert.Equal(original.Dps / 2, defended.Dps, 6);
        var unreachable = Combat.Estimate(settings, gear, 1302000, target with { Avoid = 999 }, false);
        Assert.Equal(0, unreachable.Dps);
    }

    [Fact]
    public void Wiki_parser_ignores_placeholders_and_keeps_distinct_monster_profiles()
    {
        const string html = """
            <tr><td></td><td><a href="/wiki/Snail"><span>Snail</span></a></td><td>1</td><td>45</td><td>30</td><td>2</td><td>3</td><td>0</td><td>0</td><td>33</td><td>0</td><td></td><td></td></tr>
            <tr><td></td><td><a href="/wiki/King_Slime">King Slime</a></td><td>32</td><td>16,820</td><td>100</td><td>2400</td><td>288</td><td>15</td><td>15</td><td>121</td><td>11</td><td>None</td><td>Boss</td></tr>
            <tr><td></td><td><a href="/wiki/Placeholder">Placeholder</a></td><td>32</td><td>unknown</td><td>100</td><td>2400</td><td>288</td><td>15</td><td>15</td><td>121</td><td>11</td><td>None</td></tr>
            """;
        var targets = ClassicWiki.ReadMonsters(html);
        Assert.Equal(2, targets.Length);
        Assert.Equal(16820, targets[1].Hp);
        Assert.Equal(11, targets[1].Avoid);
        Assert.Equal("https://mapleclassic.wiki/wiki/King_Slime", targets[1].Source);
    }
}

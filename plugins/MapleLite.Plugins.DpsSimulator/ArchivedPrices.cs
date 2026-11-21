using MapleLite.Core.Game;
using MapleLite.Core.Market;
using MapleLite.Sdk;

namespace MapleLite.Plugins.DpsSimulator;

internal static class ArchivedPrices
{
    public static MarketQuote? Find(MarketDatabase database, PlannedGear gear)
    {
        if (!File.Exists(database.Path)) return null;
        string? profile = string.IsNullOrWhiteSpace(gear.RemainingEnhancements) ? null : EquipmentReader.Profile(gear.Stats, gear.RemainingEnhancements);
        var manual = database.AskingPrices(gear.ItemId).FirstOrDefault(p => profile != null ? p.Profile == profile : p.Profile.Length == 0);
        if (manual != null && MesoAmount.Parse(manual.Price) is > 0 and var asking)
            return new(asking, "Saved Shop Card ask");
        var observations = database.Recent(gear.ItemId);
        var exact = observations.FirstOrDefault(o => o.Source == "FM" && o.UnitPrice > 0 && profile != null && !string.IsNullOrWhiteSpace(o.Stats)
            && EquipmentReader.Profile(o.Stats, o.Slots) == profile);
        if (exact != null) return new(exact.UnitPrice, $"Archived FM ask · matching stats · {exact.Utc:yyyy-MM-dd}");
        var latest = observations.FirstOrDefault(o => o.UnitPrice > 0 || o.Typical > 0 || o.Median10 > 0 || o.Last > 0 || o.Average3 > 0);
        if (latest == null) return null;
        long price = new[] { latest.UnitPrice, latest.Typical, latest.Median10, latest.Last, latest.Average3 }.First(v => v > 0);
        return new(price, $"Archived {latest.Source} · roll not matched · {latest.Utc:yyyy-MM-dd}");
    }
}

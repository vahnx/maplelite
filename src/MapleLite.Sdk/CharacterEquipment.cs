namespace MapleLite.Sdk;

public sealed record WornItem(string Slot, int ItemId, string Stats, string RemainingEnhancements);

public sealed record EquippedLoadout(string Character, IReadOnlyList<WornItem> Items)
{
    public const string PluginId = "dps-simulator";
    public const string FileName = "equipped";
}

/// <summary>Read-only lookup shared with the Shop Card. A quote describes the source and whether the roll matched.</summary>
public sealed record MarketQuote(long Mesos, string Source);

public sealed class MarketLookup
{
    public Func<int, string, string, MarketQuote?>? Find { get; set; }
    public Func<int, bool>? Owns { get; set; }
    public event Action? Changed;
    public void Refresh() => Changed?.Invoke();
}

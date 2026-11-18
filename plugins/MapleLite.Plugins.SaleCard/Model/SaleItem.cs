using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace MapleLite.Plugins.SaleCard;

/// <summary>One card on the sale list. Same fields and rules as the Sale Card Generator app.</summary>
public sealed class SaleItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (var a in also) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(a));
    }

    public string Key = "";
    public int ItemId;
    public string Tab = "";
    public int[]? IconPx;    // the slot picture of an unrecognised item, kept so it can be saved
    public int Stacks = 1;   // identical stacks this card stands for
    public string Category = Categories.Unknown;
    public string? Url;
    IImage? _icon;
    public IImage? Icon { get => _icon; set => Set(ref _icon, value); }
    public int SlotRow { get; set; } = -1;
    public int SlotCol { get; set; } = -1;
    public bool IsEquipment => ItemId / 1000000 == 1 || Tab == "Equip";
    /// <summary>A listing read from your own Free Market shop: it can be bought there right now.</summary>
    public bool InShop => Tab == SaleCardEngine.ShopTab;
    /// <summary>Upgrade scrolls: their tooltip (success rate and effect) is read onto the card like equipment stats.</summary>
    public bool IsScroll => ItemId / 10000 is 204 or 249;
    /// <summary>An item on your Buying list (from the item database, not read from the game).</summary>
    public bool IsWanted => Tab == SaleCardEngine.BuyTab;
    /// <summary>
    /// Cards that carry stats: equipment always (with "not recorded" until read), scrolls once read, and items you're
    /// buying when you've said what you want.
    /// </summary>
    public bool ShowsDetails => IsWanted ? !string.IsNullOrWhiteSpace(Stats) || WantedSlots != null
        : IsEquipment || (IsScroll && !string.IsNullOrWhiteSpace(Stats));
    int? WantedSlots => int.TryParse(Slots, out int n) && n >= 0 && n <= 99 ? n : null;

    string _stats = "", _slots = "", _rateNote = "";
    public string Stats { get => _stats; set => Set(ref _stats, value, nameof(Stats), nameof(DetailsText)); }
    public string Slots { get => _slots; set => Set(ref _slots, value, nameof(Slots), nameof(DetailsText)); }
    /// <summary>What the monitor last wrote from a tooltip; anything else in Stats/Slots was typed by the user.</summary>
    public string AutoStats = "", AutoSlots = "";
    public string RateNote { get => _rateNote; set => Set(ref _rateNote, value); }
    public bool HasDetails => !string.IsNullOrWhiteSpace(Stats) && int.TryParse(Slots, out int n) && n >= 0 && n <= 99;
    public string DetailsText => IsWanted ? WantedText
        : IsEquipment
        ? (string.IsNullOrWhiteSpace(Stats) ? "Tooltip not recorded — hover to read stats" : Stats.Trim()) + "\nSlots remaining: " +
          (int.TryParse(Slots, out int n) && n >= 0 && n <= 99 ? n.ToString() : "not recorded")
        : IsScroll ? Stats.Trim() : "";

    /// <summary>What you want in an item you're buying, as the card shows it: "Wants:", your stat lines, and slots if set.</summary>
    public string WantedText
    {
        get
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(Stats)) lines.AddRange(Stats.Trim().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            if (WantedSlots is int n) lines.Add($"Slots remaining: {n} or more");
            return lines.Count == 0 ? "" : "Wants:\n" + string.Join("\n", lines);
        }
    }

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    string _qty = "";
    public string Qty { get => _qty; set => Set(ref _qty, value); }

    /// <summary>The quantity badge is drawn only for more than one: "×1" says nothing.</summary>
    public bool ShowsQty => !string.IsNullOrWhiteSpace(Qty) && Qty.Trim() != "1";

    /// <summary>
    /// How many the card's price applies to: "12" is 12, "12 ×3" three stacks of 12 (36), blank one. Throwing stars and
    /// bullets are sold by the set (one slot, recharged at a shop), so for them it's the sets: "500 ×8" is 8.
    /// Null when the quantity box holds something that isn't a count.
    /// </summary>
    public long? Pieces
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Qty)) return 1;
            var m = System.Text.RegularExpressions.Regex.Match(Qty.Trim(), @"^(\d+)(?:\s*[×x]\s*(\d+))?$");
            if (!m.Success) return null;
            long stacks = m.Groups[2].Success ? long.Parse(m.Groups[2].Value) : 1;
            return ItemId / 10000 is 207 or 233 ? stacks : long.Parse(m.Groups[1].Value) * stacks;
        }
    }

    bool _include = true;
    public bool Include { get => _include; set => Set(ref _include, value, nameof(Include), nameof(CardOpacity)); }
    public double CardOpacity => Include ? 1.0 : 0.45;

    bool _buying;
    /// <summary>You want to buy this rather than sell it: the card tags it BUYING (blue) instead of SALE (pink).</summary>
    public bool Buying { get => _buying; set => Set(ref _buying, value, nameof(Buying), nameof(TradeLabel)); }
    public string TradeLabel => Buying ? "Buying" : "Sale";

    string _custom = "";
    public string Custom { get => _custom; set => Set(ref _custom, value, nameof(Custom), nameof(FinalPrice)); }

    string _rate = "";
    /// <summary>The going rate shown under the price box ("FM asking: 88.9k (5 seen)"), or empty.</summary>
    public string Rate { get => _rate; set => Set(ref _rate, value, nameof(Rate), nameof(FinalPrice)); }
    /// <summary>Each source behind the going rate ("FM", "meso.watch", ...) and its figure, for the "use this price" buttons.</summary>
    public IReadOnlyList<(string Label, long Price)> RateParts = Array.Empty<(string, long)>();
    public long? RateValue;
    public bool FromSite;   // the going rate shown came from a price site

    /// <summary>What the card prints: the price you typed, else the going rate, else "Offer".</summary>
    public string FinalPrice =>
        !string.IsNullOrWhiteSpace(Custom) ? Custom.Trim() : RateValue is long v ? Mesos.Format(v) : "Offer";

    /// <summary>The not-for-sale identity: "id:N" for a known item, "pic:HASH" for an unrecognised slot picture.</summary>
    public string Ident => ItemId != 0 ? "id:" + ItemId : Key.Contains(":?") ? "pic:" + Key[(Key.IndexOf(":?") + 2)..] : "";
}

namespace MapleLite.Plugins.SaleCard;

/// <summary>A card as saved in session.json (same fields as the Sale Card Generator app).</summary>
public sealed class SavedItem
{
    public string Key { get; set; } = ""; public int ItemId { get; set; } public string Tab { get; set; } = "";
    public string Name { get; set; } = ""; public string Qty { get; set; } = ""; public bool Include { get; set; } = true;
    public string Custom { get; set; } = ""; public string Stats { get; set; } = ""; public string Slots { get; set; } = "";
    public int SlotRow { get; set; } = -1; public int SlotCol { get; set; } = -1;
    public string AutoStats { get; set; } = ""; public string AutoSlots { get; set; } = ""; public int Stacks { get; set; } = 1;
    /// <summary>You want to buy this rather than sell it (a BUYING tag on the card).</summary>
    public bool Buying { get; set; }
    /// <summary>This item's own price adjustment on the card, in percent (100 = as listed).</summary>
    public int PricePercent { get; set; } = 100;
    /// <summary>You picked the item yourself (added by hand or corrected): tooltips never rename it.</summary>
    public bool Picked { get; set; }
    public bool PriceFlagged { get; set; }
    public string? Icon { get; set; }   // picture of an unrecognised item (32x32 BGRA, base64)
}

/// <summary>
/// The list and every setting, saved as session.json. Field names match the Sale Card Generator app, so its
/// session.json loads as is. Fields that only made sense in the app (its own game-window picker) are kept so they
/// round-trip but aren't used: MapleLite's Game page picks the window.
/// </summary>
public sealed class Session
{
    public bool ManualAvatar { get; set; }
    public Dictionary<string, bool> GroupChoices { get; set; } = new();
    public string Title { get; set; } = ""; public string Seller { get; set; } = ""; public string Channel { get; set; } = "";
    public string Location { get; set; } = ""; public string? AutoLocation { get; set; } public string? AutoIgn { get; set; }
    /// <summary>The minimap was last seen at its middle size, which hides the map's name: the card leaves the location off too.</summary>
    public bool MapNameHidden { get; set; }
    public string Search { get; set; } = ""; public string Show { get; set; } = ""; public string Tab { get; set; } = "";
    public string Server { get; set; } = "Windia";
    public bool HidePrices { get; set; } public bool Obo { get; set; } public bool SplitStacks { get; set; } public bool NoTotal { get; set; }
    public bool MonitorOff { get; set; }
    /// <summary>The app's "Browser price pages" switch (on by default there). Not used: see <see cref="BrowserTabs"/>.</summary>
    public bool BrowserOff { get; set; }
    /// <summary>Read price pages showing in the user's own browser. Opt-in: off until the user turns it on.</summary>
    public bool BrowserTabs { get; set; }
    public int Figure { get; set; } public int FigureV { get; set; } = 2;
    public bool NoFm { get; set; } public bool UseMeso { get; set; } public bool UseCozy { get; set; } public bool UseNia { get; set; } public bool UseClassic { get; set; }
    public string? Avatar { get; set; } public string AvatarFor { get; set; } = ""; public bool AvatarClean { get; set; } public bool AvatarBuilt { get; set; } public bool NoAvatar { get; set; }
    public bool? ShowPrices { get; set; } public string GameName { get; set; } = ""; public int GamePid { get; set; }
    /// <summary>Compact tiles are the default. CompactDefaulted marks a session that has had that default applied once.</summary>
    public bool CompactTiles { get; set; } = true; public bool CompactDefaulted { get; set; }
    public bool HideOffer { get; set; } public bool NoStats { get; set; } public bool NoTime { get; set; }
    /// <summary>Tag items live in your Free Market shop, and say how to buy the rest (Discord or a whisper). On unless unticked.</summary>
    public bool NoShopMarker { get; set; }
    /// <summary>Your Discord name for the card's "how to buy" line; empty leaves it as "DM me on Discord".</summary>
    public string Discord { get; set; } = "";
    public bool LocalTime { get; set; } public string Zone { get; set; } = "EST"; public bool MadeWith { get; set; }
    public string Order { get; set; } = "";
    public string? Tagline { get; set; } = "DISCOUNT SHOP";
    /// <summary>
    /// The Selling list's sort column ("on", "name", "qty", "price"; empty is the order found) and direction. Checked
    /// items first ("on") by default; ListSortDefaulted marks a session that has had that default applied once.
    /// </summary>
    public string ListSort { get; set; } = "on";
    public bool ListSortDefaulted { get; set; }
    public bool ListSortDown { get; set; }
    /// <summary>The page's open tab: 0 Selling, 1 Buying, 2 Design, 3 Settings (SectionV 2; before that 1 was Design).</summary>
    public int Section { get; set; }
    public int SectionV { get; set; }
    /// <summary>Take every selling item, or every item you're buying, off the card at once (the top bar's switches).</summary>
    public bool HideSelling { get; set; }
    public bool HideBuying { get; set; }
    /// <summary>The Buying tab's search text.</summary>
    public string BuySearch { get; set; } = "";
    public string SellSource { get; set; } = "All sources";
    public string SellType { get; set; } = "All types";
    public string CardSearch { get; set; } = "";
    public string CardSource { get; set; } = "All sources";
    public string CardType { get; set; } = "All types";
    public string BuyCardSearch { get; set; } = "";
    public string BuyCardType { get; set; } = "All types";
    public string BuySort { get; set; } = "name";
    public bool BuySortDown { get; set; }
    public List<string> ManualOrder { get; set; } = new();
    public string BuyType { get; set; } = "All types";
    /// <summary>Every price on the card (and the total) at this percent of the price in the list: 1 to 200, 100 unchanged.</summary>
    public int PricePercent { get; set; } = 100;
    /// <summary>Tag each tile SALE (pink) or BUYING (blue). On unless unticked.</summary>
    public bool NoTradeTags { get; set; }
    /// <summary>Price adjustment per kind of item ("Armor" → 90), in percent; kinds not listed are at 100.</summary>
    public Dictionary<string, int> KindPercent { get; set; } = new();
    /// <summary>Group the tiles by kind (Weapons, Armor, Gems &amp; ores…), each group under a divider. On unless unticked.</summary>
    public bool NoGroups { get; set; }
    /// <summary>The total split by kind ("Armor 200k · Weapons 50k"), above the overall total. On unless unticked.</summary>
    public bool NoKindTotals { get; set; }
    /// <summary>Tag each item you sell with its discount ("-20%") while the price slider is below 100%. On unless unticked.</summary>
    public bool NoDiscountTags { get; set; }
    /// <summary>Tag colours ("#RRGGBB"): SALE, BUYING, IN SHOP and the discount tag. The tags' text stays white.</summary>
    public string SaleTagColour { get; set; } = CardRenderer.TagColours.SaleDefault;
    public string BuyTagColour { get; set; } = CardRenderer.TagColours.BuyDefault;
    public string ShopTagColour { get; set; } = CardRenderer.TagColours.ShopDefault;
    public string DiscountTagColour { get; set; } = CardRenderer.TagColours.DiscountDefault;
    /// <summary>Pack tiles into the shortest column at their own height (no gaps, nothing stretched). On unless unticked.</summary>
    public bool NoPack { get; set; }
    /// <summary>"ea" after the price of cards holding more than one (×2 chairs at "9,999 ea"). On unless unticked.</summary>
    public bool NoEach { get; set; }
    /// <summary>"Seller location": circle your yellow dot on the map, with an arrow and your IGN. On unless unticked.</summary>
    public bool NoSellerMarker { get; set; }
    /// <summary>Seller location colours ("#RRGGBB"): the ring and arrow, the name tag, and the tag's text.</summary>
    public string MarkerRing { get; set; } = "#E5332A"; public string MarkerTagColour { get; set; } = "#E5332A"; public string MarkerText { get; set; } = "#FFFFFF";
    /// <summary>Where you dragged the name tag: its centre relative to your dot, in map pixels. Null places it automatically.</summary>
    public double[]? MarkerTag { get; set; }
    /// <summary>Trim plain bands (sky, empty bars) off the left and right of the minimap picture.</summary>
    public bool MapTrim { get; set; }
    /// <summary>The part of the (trimmed) map to show, as shares of its width and height: x, y, width, height. Null shows all of it.</summary>
    public double[]? MapCrop { get; set; }
    /// <summary>How large the map is drawn on the card, in percent of the room it has (40 to 100).</summary>
    public int MapScale { get; set; } = 100;
    public bool NoMap { get; set; } public string Theme { get; set; } = ""; public string? Map { get; set; } public int MapW { get; set; } public int MapH { get; set; }
    public List<SavedItem> Items { get; set; } = new();
    public HashSet<string> DismissedItems { get; set; } = new();
    /// <summary>Your own Free Market shop was open when last seen; when it's gone, its unsold listings go back to the inventory.</summary>
    public bool ShopOpen { get; set; }
    /// <summary>Sold listings of your own shop already taken off the list ("SELLER:item:bundle:price:n").</summary>
    public HashSet<string> ShopSold { get; set; } = new();
    /// <summary>
    /// Items you corrected: what the scanner reads ("id:N" for a recognised icon, "pic:HASH" for an unrecognised slot
    /// picture) and the item it really is. Applied to every scan, so the fix sticks. Scrolls share icons, so they are
    /// corrected per slot instead (see SaleItem.Picked).
    /// </summary>
    public Dictionary<string, int> Renames { get; set; } = new();
    /// <summary>Manual identities keyed by the original shop listing, preserving rescan and sold detection.</summary>
    public Dictionary<string, int> ShopRenames { get; set; } = new();

    // Settings as the page uses them.
    public bool PricesShown => ShowPrices ?? !HidePrices;
    public bool SourceOn(string site) => site switch
    {
        PriceSites.Meso => UseMeso, PriceSites.Cozy => UseCozy, PriceSites.Nia => UseNia, PriceSites.Classic => UseClassic, _ => false,
    };
    public void SetSource(string site, bool on)
    {
        switch (site)
        {
            case PriceSites.Meso: UseMeso = on; break;
            case PriceSites.Cozy: UseCozy = on; break;
            case PriceSites.Nia: UseNia = on; break;
            case PriceSites.Classic: UseClassic = on; break;
        }
    }
    /// <summary>Which figure to take from the sites (0 usual, 1 last sale, 2 last 3 avg, 3 last 10 median); older files counted from 1.</summary>
    public int SiteFigure => Math.Clamp(FigureV >= 2 ? Figure : Figure + 1, 0, 3);

    internal static byte[]? Bytes(string? base64)
    {
        if (base64 == null) return null;
        try { return Convert.FromBase64String(base64); } catch (FormatException) { return null; }
    }

    internal static int[]? Pixels(string? base64, int count)
    {
        var raw = Bytes(base64);
        if (raw == null || raw.Length != count * 4) return null;
        var px = new int[count];
        Buffer.BlockCopy(raw, 0, px, 0, raw.Length);
        return px;
    }

    internal static string? Base64(int[]? px) => px == null ? null : Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()));
}

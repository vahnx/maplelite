using MapleLite.Core.Game;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MapleLite.Core.Capture;
using MapleLite.Core.Imaging;
using MapleLite.Sdk;

namespace MapleLite.Plugins.SaleCard;

/// <summary>
/// The Sale Card plugin's logic (the Sale Card Generator app's main window, minus the window): watches the game once a
/// second for the inventory, storage, hovered tooltips, player shops, minimap, IGN, channel and portrait; keeps the
/// list, the price records and the session; and works out each card's going rate.
/// Everything that touches <see cref="Items"/> runs on the UI thread.
/// </summary>
public sealed class SaleCardEngine
{
    public const string StorageTab = "Storage";
    public const string ShopTab = "My FM shop";
    /// <summary>Cards for items you want to buy, picked from the item database on the Buying tab (Key "Buy:{id}").</summary>
    public const string BuyTab = "Buying";
    public const string PlaceBag = "In inventory", PlaceStorage = "In storage", PlaceShop = "In my FM shop";
    public static string PlaceOf(SaleItem item) => item.Tab == ShopTab ? PlaceShop : item.Tab == StorageTab ? PlaceStorage : PlaceBag;
    public static readonly string[] Orders = { "By type", "By name", "By price (high first)", "As scanned" };
    public static readonly string[] SiteModes = { "Usual / typical", "Last sale", "Last 3 avg", "Last 10 median" };

    readonly IPluginContext _ctx;
    readonly string _folder;
    public ObservableCollection<SaleItem> Items { get; } = new();
    public Session S { get; private set; } = new();
    public Vision? Vision { get; private set; }
    PriceBook _book;
    HoverBook _hover;
    NotForSale _notForSale;
    FmPrices _fm;
    SitePrices _site;
    readonly ShopReader _shops = new();
    IPriceSource? _browser;

    /// <summary>Status line for the page; Changed fires when it, the list's filters, or the settings change.</summary>
    public string Status { get; private set; } = "Starting…";
    public string BrowserStatus { get; private set; } = "";
    public event Action? Changed;
    /// <summary>Something on the card changed: the page redraws its preview (debounced).</summary>
    public event Action? CardChanged;

    public SaleCardEngine(IPluginContext ctx)
    {
        _ctx = ctx;
        _folder = ctx.Storage.Folder;
        Directory.CreateDirectory(_folder);
        (_book, _hover, _notForSale, _fm, _site) = (new(_folder), new(_folder), new(_folder), new(_folder), new(_folder));
        Items.CollectionChanged += (_, _) => CardChanged?.Invoke();
    }

    public string DataFolder => _folder;

    void Say(string status) { Status = status; Changed?.Invoke(); }

    // ---------------------------------------------------------------- start / stop

    DispatcherTimer? _watch, _browserTimer;
    IDisposable? _frames;

    public async Task StartAsync()
    {
        Say("Loading item icons…");
        var items = _ctx.Items;
        Vision = await Task.Run(() => new Vision(items));
        LoadSession();
        // Keep the host's capture loop running at least once a second while this plugin is on.
        _frames = _ctx.Frames.Subscribe(_ => { }, 1);
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watch.Tick += async (_, _) => await TickAsync();
        _browserTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _browserTimer.Tick += async (_, _) => await BrowserTickAsync();
        _browser = new BrowserTabSource(_ctx.Ocr, _ctx.OtherWindows);
        if (Vision.TemplateCount == 0) Say($"No item icons found in the app's {ItemCatalog.ShippedFolder} folder: items can't be named automatically.");
        else
        {
            Say($"Ready ({Vision.TemplateCount} items known).");
            SetMonitor(!S.MonitorOff);
        }
        _browserTimer.Start();
        BrowserStatus = S.BrowserTabs ? "" : "off";
    }

    public Task StopAsync()
    {
        _watch?.Stop(); _browserTimer?.Stop();
        _frames?.Dispose();
        _fm.Save(); _site.Save(); SaveSession();
        return Task.CompletedTask;
    }

    public bool Monitoring => _watch?.IsEnabled == true;

    public void SetMonitor(bool on)
    {
        S.MonitorOff = !on;
        if (_watch == null || on == _watch.IsEnabled) { Changed?.Invoke(); return; }
        if (on)
        {
            _gridHash = 0; _storageHash = 0; _lastLay = null; _hoverDone = null;
            _watch.Start();
            Say("Watching the game. Open your inventory and click through its tabs; hover equipment to read its stats.");
        }
        else
        {
            _watch.Stop();
            _fm.Save();
            Say("Not watching the game. Turn on Watch the game to start again.");
        }
        SaveSession();
    }

    public void SetBrowserTabs(bool on)
    {
        S.BrowserTabs = on;
        BrowserStatus = on ? "looking for a price site tab…" : "off";
        SaveSession();
        Changed?.Invoke();
    }

    /// <summary>The newest game picture, or null with the reason in the status line.</summary>
    Frame? Latest()
    {
        var latest = _ctx.Frames.Latest;
        if (latest == null || DateTimeOffset.Now - latest.Time > TimeSpan.FromSeconds(5))
        {
            var st = _ctx.Frames.Status;
            Say(st.State == CaptureState.Capturing ? "Waiting for a game picture…" : st.Message);
            return null;
        }
        return latest.Frame;
    }

    (int x, int y)? Cursor() => _ctx.CursorInGame() is { } c ? (c.X, c.Y) : null;

    // ---------------------------------------------------------------- session

    string SessionFile => Path.Combine(_folder, DataFiles.Session);
    bool _sessionReady;   // nothing is written until the saved list has been read back
    bool _bulk;

    void LoadSession()
    {
        try
        {
            S = File.Exists(SessionFile) ? JsonSerializer.Deserialize<Session>(Store.Read(SessionFile)) ?? new() : new();
            S.GroupChoices ??= new();
            // Compact tiles became the default: sessions saved before that switch once (unticking it later is kept).
            if (!S.CompactDefaulted) { S.CompactTiles = true; S.CompactDefaulted = true; }
            // checked items first by default; a sort you pick afterwards (even back to the order found) is kept
            if (!S.ListSortDefaulted) { if (S.ListSort.Length == 0) { S.ListSort = "on"; S.ListSortDown = false; } S.ListSortDefaulted = true; }
            S.ShopSold ??= new();
            // Lists saved before this was remembered: what is there came from the game, so keep following it.
            S.AutoLocation ??= S.Location;
            S.AutoIgn ??= S.Seller;
            if (Session.Pixels(S.Map, S.MapW * S.MapH) is { } map && S.MapW > 0) SetMap(map, S.MapW, S.MapH);
            if (Session.Pixels(S.Avatar, AvatarReader.W * AvatarReader.H) is { } av) SetAvatar(av, S.AvatarFor, S.AvatarClean, S.AvatarBuilt);
            _bulk = true;
            foreach (var s in S.Items)
            {
                if (Items.Any(i => i.Key == s.Key)) continue;
                var iconPx = Session.Pixels(s.Icon, Vision.Slot * Vision.Slot);
                // unknown cards cut from a grid that was out of place, or from an empty slot, are not items
                if (s.ItemId == 0 && iconPx != null && (Vision.Offset(iconPx) || Vision.Blank(iconPx))) continue;
                var item = new SaleItem
                {
                    Key = s.Key, ItemId = s.ItemId, Tab = s.Tab, Category = Categories.Of(s.ItemId), Stacks = s.Stacks,
                    Name = s.Name, Qty = s.Qty, Include = s.Include, Custom = s.Custom, Stats = s.Stats, Slots = s.Slots, SlotRow = s.SlotRow, SlotCol = s.SlotCol,
                    AutoStats = s.AutoStats, AutoSlots = s.AutoSlots, IconPx = iconPx, Buying = s.Buying,
                };
                if (_notForSale.Has(item.Ident)) continue;
                item.Icon = Icon(s.ItemId, iconPx);
                if (s.ItemId != 0 && Vision!.Catalog.TryGetValue(s.ItemId, out var cat)) item.Url = ItemLinks.Kerning(cat);
                // Re-spell with the current rules, so older saved cards pick up fixes.
                if (item.Stats == item.AutoStats && item.Stats.Length > 0) item.Stats = item.AutoStats = EquipmentReader.FixLabels(item.Stats);
                ApplyRate(item);
                Add(item);
            }
            PruneDuplicateCards();
            MoveOldBuyingMarks();
            // Listings kept while the shop isn't known to be open (saved before it was tracked, or it closed unseen) can never be
            // seen to close, so they go back to the inventory now. Seeing the shop open again puts them back in it.
            if (!S.ShopOpen) ReturnShopItems();
            // what the saved cards know goes into the memory; cards saved without stats are filled from it
            foreach (var i in Items.Where(i => i.IsEquipment && !i.IsWanted && !string.IsNullOrWhiteSpace(i.Stats))) _hover.Set(i.ItemId, i.Key, i.Stats, i.Slots);
            foreach (var i in Items.ToList()) Recall(i);
            if (!(S.Tab.Length == 0 || Vision.TabNames.Contains(S.Tab) || S.Tab == StorageTab || S.Tab == ShopTab)) S.Tab = "";
        }
        catch { Store.Failed(SessionFile); S = new(); /* starting empty, with the unreadable file kept aside */ }
        finally { _bulk = false; }
        _sessionReady = true;
        SaveSession();
        Changed?.Invoke();
    }

    public void SaveSession()
    {
        if (!_sessionReady) return;
        try
        {
            S.Map = Session.Base64(_mapPx); S.MapW = _mapW; S.MapH = _mapH;
            S.Avatar = Session.Base64(_avatarPx); S.AvatarFor = _avatarFor; S.AvatarClean = _avatarClean; S.AvatarBuilt = _avatarBuilt;
            S.Items = Items.Select(i => new SavedItem
            {
                Key = i.Key, ItemId = i.ItemId, Tab = i.Tab, Name = i.Name, Qty = i.Qty, Include = i.Include, Custom = i.Custom,
                Stats = i.Stats, Slots = i.Slots, SlotRow = i.SlotRow, SlotCol = i.SlotCol, AutoStats = i.AutoStats, AutoSlots = i.AutoSlots, Stacks = i.Stacks,
                Icon = Session.Base64(i.IconPx), Buying = i.Buying,
            }).ToList();
            Store.Write(SessionFile, JsonSerializer.Serialize(S));
        }
        catch { /* a failed save must never interrupt the plugin */ }
    }

    void Add(SaleItem item) { item.PropertyChanged += OnItemChanged; Items.Add(item); }
    void Remove(SaleItem item) { item.PropertyChanged -= OnItemChanged; Items.Remove(item); }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SaleItem it) return;
        if (e.PropertyName == nameof(SaleItem.Custom) && it is { ItemId: not 0, IsEquipment: false, Buying: false }) _book.Set(it.ItemId, it.Custom);
        if (e.PropertyName is nameof(SaleItem.Stats) or nameof(SaleItem.Slots))
        {
            ApplyRate(it);
            // every reading (hovered or typed) is remembered for the next time a card is made for this item
            // (not what you want in an item you are buying: that is a wish, not a reading)
            if (it.IsEquipment && !it.IsWanted && !string.IsNullOrWhiteSpace(it.Stats)) _hover.Set(it.ItemId, it.Key, it.Stats, it.Slots);
        }
        if (e.PropertyName == nameof(SaleItem.Include) && !_bulk) Changed?.Invoke();
        if (e.PropertyName is nameof(SaleItem.Icon) or nameof(SaleItem.RateNote)) { }
        CardChanged?.Invoke();
        QueueSave();
    }

    DispatcherTimer? _saveTimer;
    void QueueSave()
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _saveTimer!.Stop(); SaveSession(); });
        _saveTimer.Stop(); _saveTimer.Start();
    }

    /// <summary>Call after changing a setting in <see cref="S"/> from the page.</summary>
    public void SettingsChanged(bool rates = false)
    {
        if (rates) foreach (var i in Items) ApplyRate(i);
        CardChanged?.Invoke();
        QueueSave();
    }

    void PruneDuplicateCards()
    {
        var remove = new HashSet<SaleItem>();
        // Older tooltip corrections left the corrected scroll in an impossible tab, beside the icon guess in Use.
        // Keep the corrected title and the actual Use stack count as one card.
        foreach (var corrected in Items.Where(i => i.ItemId / 10000 == 204 && i.Tab != "Use" && i.Tab != StorageTab && i.Tab != ShopTab && i.Tab != "Items").ToList())
        {
            var guess = Items.FirstOrDefault(i => i.Tab == "Use" && i.ItemId / 10000 == 204 && i.SlotRow == corrected.SlotRow && i.SlotCol == corrected.SlotCol);
            if (guess == null) continue;
            corrected.Tab = "Use"; corrected.Qty = guess.Qty; corrected.Stacks = guess.Stacks;
            corrected.Key = $"Use:{corrected.ItemId}:{corrected.Stacks}:{corrected.Qty}";
            remove.Add(guess);
        }
        // "Items" is the fallback name used before the active game tab was known.
        // Once the same item exists under a real tab, keep the real-tab card only.
        foreach (var group in Items.Where(i => i.ItemId != 0).GroupBy(i => (i.ItemId, i.Qty, i.Custom)))
            if (group.Any(i => i.Tab == "Items") && group.Any(i => i.Tab != "Items")) remove.UnionWith(group.Where(i => i.Tab == "Items"));
        // The same for unrecognised items: the key ends with the slot picture's fingerprint.
        foreach (var group in Items.Where(i => i.ItemId == 0 && i.Key.Contains(":?")).GroupBy(i => i.Key[i.Key.IndexOf(":?")..]))
            if (group.Any(i => i.Tab == "Items") && group.Any(i => i.Tab != "Items")) remove.UnionWith(group.Where(i => i.Tab == "Items"));
        // Equipment copies are distinct only when their recorded rolls/slots differ.
        foreach (var group in Items.Where(i => i.IsEquipment && i.Tab != StorageTab && i.Tab != ShopTab && i.Tab != BuyTab).GroupBy(i => (i.ItemId, i.Stats, i.Slots)))
            if (group.Count() > 1)
            {
                var keep = group.OrderBy(i => i.Tab == "Equip" ? 0 : i.Tab == "Items" ? 1 : 2).First();
                remove.UnionWith(group.Where(i => i != keep));
            }
        foreach (var item in remove) Remove(item);
    }

    // ---------------------------------------------------------------- icons, portrait, map

    readonly Dictionary<int, IImage?> _iconCache = new();
    IImage? Icon(int id, int[]? px)
    {
        if (id != 0 && Vision != null)
        {
            if (!_iconCache.TryGetValue(id, out var icon))
            {
                try { icon = Vision.IconPath(id) is { } path ? new Bitmap(path) : null; } catch { icon = null; }
                _iconCache[id] = icon;
            }
            if (icon != null) return icon;
        }
        return px != null ? CardRenderer.Bitmap(px, Vision.Slot, Vision.Slot) : null;
    }

    string _avatarFor = "";      // the IGN the portrait was found by
    bool _avatarClean;           // a portrait from a storage or shop window, not a cut-out of the world
    bool _avatarBuilt;           // pieced together from many cut-outs of the world, scenery already removed
    int[]? _avatarPx;            // as captured
    public int[]? AvatarShown { get; private set; }   // as drawn on the card (backdrop removed when clean)
    readonly PortraitBuilder _portrait = new();
    string _portraitFor = "";
    int _avatarLook;             // which way of looking for the name tag to try next

    int[]? _mapPx; int _mapW, _mapH; DateTime _mapAt;
    public (int[] px, int w, int h)? Map => _mapPx == null ? null : (_mapPx, _mapW, _mapH);

    /// <summary>The minimap picture with its plain sides trimmed off when that is on: what the Design page crops.</summary>
    public (int[] px, int w, int h)? MapTrimmed()
    {
        if (Map is not { } m) return null;
        if (!S.MapTrim) return m;
        var frame = new Frame(m.w, m.h, m.px);
        var keep = MapleLite.Core.Imaging.ImageOps.TrimPlain(frame);
        if (keep == frame.Bounds) return m;
        return (frame.Crop(keep).Px, keep.Width, keep.Height);
    }

    /// <summary>The map as the card shows it: trimmed, then cropped to the part picked in Design.</summary>
    public (int[] px, int w, int h)? CardMap()
    {
        if (MapTrimmed() is not { } m) return null;
        if (S.MapCrop is not { Length: 4 } c) return m;
        int x = Math.Clamp((int)Math.Round(c[0] * m.w), 0, m.w - 1), y = Math.Clamp((int)Math.Round(c[1] * m.h), 0, m.h - 1);
        int w = Math.Clamp((int)Math.Round(c[2] * m.w), 1, m.w - x), h = Math.Clamp((int)Math.Round(c[3] * m.h), 1, m.h - y);
        if (w < 8 || h < 8) return m;   // too small to mean anything (a map of another size): show it whole
        return (new Frame(m.w, m.h, m.px).Crop(new MapleLite.Core.Imaging.Region(x, y, w, h)).Px, w, h);
    }

    /// <summary>The portrait for the card: only when it was found for the IGN on the card.</summary>
    public int[]? AvatarFor(string seller) => _avatarFor == seller.Trim() ? AvatarShown : null;

    void SetMap(int[] px, int w, int h) { _mapPx = px; _mapW = w; _mapH = h; _mapAt = DateTime.UtcNow; }

    void SetAvatar(int[] px, string ign, bool clean, bool built = false)
    {
        // a clean portrait loses its backdrop; a cut-out of the world has scenery that cannot be told from the character
        AvatarShown = clean ? AvatarReader.Tidy(AvatarReader.ClearBackground(px)) : px;
        _avatarPx = px; _avatarFor = ign; _avatarClean = clean; _avatarBuilt = built;
    }

    /// <summary>A portrait snipped by hand on the page. It's used from now on instead of reading one from the game.</summary>
    public void SetManualAvatar(int[] px)
    {
        _portrait.Clear(); _portraitFor = S.Seller.Trim();
        S.ManualAvatar = true; S.NoAvatar = false;
        SetAvatar(px, S.Seller.Trim(), false);
        SaveSession();
        CardChanged?.Invoke();
        Say("Picture replaced from the manual snip.");
    }

    /// <summary>Looks for your character: a window portrait is taken as is; cut-outs of the world go to the builder. Cheap tries one way per call.</summary>
    async Task ReadAvatar(Frame frame, bool cheap = false)
    {
        if (S.ManualAvatar) return;
        var ign = S.Seller.Trim();
        if (ign.Length < 3) return;
        bool held = _avatarFor == ign, best = held && _avatarClean;
        var found = await AvatarReader.ReadAsync(_ctx.Ocr, frame, ign, !cheap ? -1 : best ? 0 : _avatarLook);
        if (found == null) { if (cheap && !best) _avatarLook = (_avatarLook + 1) % 4; return; }
        var (px, clean) = found.Value;
        if (clean)
        {
            if (best && _avatarPx != null && px.AsSpan().SequenceEqual(_avatarPx)) return;
            SetAvatar(px, ign, true);
            CardChanged?.Invoke();
            return;
        }
        if (best) return;   // a window portrait is exact: keep it
        if (_portraitFor != ign) { _portrait.Clear(); _portraitFor = ign; }
        if (_portrait.Add(px) && _portrait.Build() is { } built) { SetAvatar(built, ign, false, true); CardChanged?.Invoke(); }
        else if (!held) { SetAvatar(px, ign, false); CardChanged?.Invoke(); }
    }

    // ---------------------------------------------------------------- monitor

    bool _busy;
    int _tick;
    InvLayout? _lastLay;
    long _gridHash, _storageHash;
    string? _hoverDone;   // card whose tooltip was already read during this hover

    async Task TickAsync()
    {
        if (_busy || Vision == null) return;
        _busy = true;
        try
        {
            var frame = Latest();
            if (frame == null || !Monitoring) return;
            if (ChannelReader.Read(frame) is int ch && ch.ToString() != S.Channel) { S.Channel = ch.ToString(); CardChanged?.Invoke(); Changed?.Invoke(); }
            // The minimap at its middle size hides the map's name: the card follows and leaves the location off. While the
            // minimap isn't showing at all, the last choice stands.
            var minimap = MapReader.State(frame);
            if (minimap is MinimapState.Full or MinimapState.NameHidden && (minimap == MinimapState.NameHidden) != S.MapNameHidden)
            {
                S.MapNameHidden = minimap == MinimapState.NameHidden;
                CardChanged?.Invoke(); Changed?.Invoke();
            }
            // The minimap picture: your dot moves all the time, so the card is refreshed with it every few seconds at most.
            if (MapReader.Picture(frame) is { } pic && (_mapPx == null || pic.w != _mapW || pic.h != _mapH ||
                (DateTime.UtcNow - _mapAt > TimeSpan.FromSeconds(5) && !pic.px.AsSpan().SequenceEqual(_mapPx))))
            {
                SetMap(pic.px, pic.w, pic.h);
                if (!S.NoMap) CardChanged?.Invoke();
            }
            // Follow the map as you move, unless you typed a location of your own.
            if (++_tick % 3 == 1 && (string.IsNullOrWhiteSpace(S.Location) || S.Location == S.AutoLocation))
            {
                var (_, map) = await MapReader.ReadAsync(_ctx.Ocr, frame);
                if (!string.IsNullOrWhiteSpace(map) && map != S.Location) { S.Location = S.AutoLocation = map; CardChanged?.Invoke(); Changed?.Invoke(); }
            }
            // The IGN is taken from the game's status bar; one you typed yourself is left alone.
            if (_tick % 3 == 1 && (string.IsNullOrWhiteSpace(S.Seller) || S.Seller == S.AutoIgn))
            {
                var ign = await IgnReader.ReadAsync(_ctx.Ocr, frame);
                if (!string.IsNullOrWhiteSpace(ign) && ign != S.Seller) { S.Seller = S.AutoIgn = ign; CardChanged?.Invoke(); Changed?.Invoke(); }
            }
            if (_tick % 3 == 2) await ReadAvatar(frame, cheap: true);
            var notes = new List<string>();
            if (await MonitorInventory(frame) is { } inv) notes.Add(inv);
            if (await MonitorStorage(frame) is { } sto) notes.Add(sto);
            if (await WatchShop(frame) is { } shop) notes.Add(shop);
            if (!Monitoring) return;
            Say(notes.Count > 0 ? string.Join(" · ", notes) + "." : "Watching the game: open your inventory, storage or a player shop.");
        }
        catch (Exception ex) { _ctx.Log("read failed: " + ex); Say("Read failed: " + ex.Message); }
        finally { _busy = false; }
    }

    /// <summary>Reads the game again right away (Refresh now): inventory, storage, location and portrait.</summary>
    public async Task RefreshNowAsync()
    {
        if (Vision == null || _busy) return;
        var frame = Latest();
        if (frame == null) return;
        _busy = true;
        try
        {
            _gridHash = 0; _storageHash = 0; _lastLay = null; _hoverDone = null;
            await ReadGameInfo(frame, overwrite: false);
            var notes = new List<string>();
            if (await MonitorInventory(frame) is { } inv) notes.Add(inv);
            if (await MonitorStorage(frame) is { } sto) notes.Add(sto);
            Say(notes.Count == 0 ? "Read the game again; no inventory or storage was found." : string.Join(" · ", notes));
        }
        finally { _busy = false; }
    }

    /// <summary>Manual read: IGN, location, channel and portrait, replacing what is in the boxes.</summary>
    public async Task ManualReadAsync()
    {
        var frame = Latest();
        if (frame == null) return;
        await ReadGameInfo(frame, overwrite: true);
    }

    /// <summary>The newest game picture for the portrait snip, or null with the reason in the status line.</summary>
    public Frame? FrameForSnip() => Latest();

    static bool InGrid(InvLayout l, (int x, int y)? cursor) => cursor is { } c &&
        c.x >= l.GridX && c.x < l.GridX + Vision.Cols * Vision.PitchX && c.y >= l.GridY && c.y < l.GridY + Vision.Rows * Vision.PitchY;

    static long GridHash(Frame f, InvLayout l)
    {
        long h = l.Tab + 7;
        for (int y = l.GridY; y < l.GridY + Vision.Rows * Vision.PitchY; y++)
            for (int x = l.GridX; x < l.GridX + Vision.Cols * Vision.PitchX; x++)
                h = h * 31 + f.At(x, y);
        return h;
    }

    async Task<string?> MonitorStorage(Frame frame)
    {
        var lay = await Task.Run(() => Vision.LocateStorage(frame));
        if (lay == null) return null;
        if (InGrid(lay, Cursor())) return "storage: mouse is on it, move it off to pick up changes";
        long hash = GridHash(frame, lay);
        if (hash == _storageHash) return "storage is up to date (scroll it to add the rest)";
        var (added, _) = await ScanGrid(frame, lay, StorageTab, auto: true);
        _storageHash = _unsurePending ? 0 : hash;
        return added > 0 ? $"storage: added {added} item{(added == 1 ? "" : "s")}" : "storage is up to date (scroll it to add the rest)";
    }

    async Task<string?> MonitorInventory(Frame frame)
    {
        var vision = Vision!;
        var cursor = Cursor();
        var lay = await Task.Run(() => Vision.Locate(frame));
        // A tooltip can hide enough of the grid that it isn't found: while the mouse is on it, trust where it was.
        if (lay == null && _lastLay != null && InGrid(_lastLay, cursor)) lay = _lastLay;
        _lastLay = lay;
        if (lay == null) return null;
        // An unreadable tab is not a new inventory: wait for a reliable frame.
        if (lay.Tab < 0) return "Waiting for a clear inventory tab";
        string tab = Vision.TabNames[lay.Tab];

        if (!InGrid(lay, cursor))
        {
            _hoverDone = null;
            long hash = GridHash(frame, lay);
            if (hash == _gridHash) return $"{tab} tab is up to date";
            var (added, _) = await ScanGrid(frame, lay, tab, auto: true);
            _gridHash = _unsurePending ? 0 : hash;   // look again next time if a slot was still unclear
            return added > 0 ? $"{tab} tab: added {added} item{(added == 1 ? "" : "s")}" : $"{tab} tab is up to date";
        }

        // The mouse is on the inventory, so a tooltip probably covers part of it: don't rescan, read the tooltip.
        int col = (cursor!.Value.x - lay.GridX) / Vision.PitchX, row = (cursor.Value.y - lay.GridY) / Vision.PitchY;
        var catalog = vision.Catalog.Values;
        // A scroll: its tooltip says which one it is. Kept for this slot; the list updates once the mouse moves off.
        if (_slotSeen.TryGetValue((tab, row, col), out var seenHere) && seenHere.id / 10000 == 204)
        {
            if (await ItemTooltipReader.ReadAsync(_ctx.Ocr, frame, cursor, catalog) is { } scroll && MayRename(seenHere.id, scroll, tab))
            {
                var k = (tab, row, col);
                if (!_slotTitle.TryGetValue(k, out var old) || old.id != scroll.id || old.hash != seenHere.hash)
                {
                    _slotTitle[k] = (seenHere.hash, scroll.id);
                    _gridHash = 0;   // read the tab again as soon as the mouse is off it
                }
                // the card already names this scroll: go on and read its success rate and effect
                if (!Items.Any(i => i.Tab == tab && i.ItemId == scroll.id && i.SlotRow == row && i.SlotCol == col))
                    return $"{tab} tab: this is {scroll.name} (the list updates when the mouse moves off)";
            }
        }
        var cards = Items.Where(i => i.Tab == tab && i.ItemId != 0 && ((i.SlotRow == row && i.SlotCol == col) || i.Key.StartsWith($"Equip:{row}:{col}:"))).Reverse().ToList();
        // A tooltip that says the item cannot be sold: the item is left off the list from now on.
        string spot = tab + ":" + row + ":" + col;
        if (_flagSpot != spot) { _flagSpot = spot; _flagTries = 0; }
        if (_flagTries < 4)
        {
            _flagTries++;
            if (await ItemTooltipReader.FlagAsync(_ctx.Ocr, frame, cursor) is { } flag)
            {
                _flagTries = 99;
                _flagged = (tab, row, col);
                _gridHash = 0;   // look at the tab again as soon as the pointer is off it
                return flag + ": will be left off the list (move the mouse off the inventory)";
            }
        }
        if (cards.Count == 0)
        {
            // The pointer sits on the hovered icon, so an item never seen without it has no card yet (or only an
            // "Unknown item" one). Its tooltip names it: make the card from that. An empty slot shows no tooltip, so
            // any item name read now is from something else on screen: make nothing.
            if (_emptySlots.TryGetValue(tab, out var emptyHere) && emptyHere.Contains((row, col)))
                return $"{tab} tab: mouse is on an empty slot";
            var named = tab == "Equip" ? await ItemTooltipReader.ReadAsync(_ctx.Ocr, frame, cursor, catalog) : null;
            if (named == null || named.id / 1000000 != 1) return $"{tab} tab: mouse is on the inventory, move it off to pick up changes";
            var card = Items.FirstOrDefault(i => i.Tab == tab && i.SlotRow == row && i.SlotCol == col && i.ItemId == 0);
            bool isNew = card == null;
            card ??= new SaleItem { Tab = tab, Stacks = 1, SlotRow = row, SlotCol = col };
            card.Key = $"Equip:{row}:{col}:{named.id}";
            card.ItemId = named.id;
            card.Name = named.name;
            card.Category = Categories.Of(named.id);
            card.Url = ItemLinks.Kerning(named);
            card.Icon = Icon(named.id, null) ?? card.Icon;
            card.Include = true;
            if (S.GroupChoices.TryGetValue(PlaceBag, out bool placeChoice)) card.Include = placeChoice;
            if (S.GroupChoices.TryGetValue(card.Category, out bool categoryChoice)) card.Include = categoryChoice;
            ApplyRate(card);
            if (isNew) Add(card);
            cards.Add(card);
            Changed?.Invoke();
        }
        if (await ItemTooltipReader.ReadAsync(_ctx.Ocr, frame, cursor, catalog) is { } hovered &&
            cards.FirstOrDefault(c => c.Name != hovered.name && MayRename(c.ItemId, hovered, tab)) is { } corrected)
        {
            corrected.ItemId = hovered.id;
            corrected.Name = hovered.name;
            corrected.Category = Categories.Of(hovered.id);
            corrected.Url = ItemLinks.Kerning(hovered);
            corrected.Icon = Icon(hovered.id, null) ?? corrected.Icon;
            if (corrected.Tab != "Equip") corrected.Key = $"{corrected.Tab}:{hovered.id}:{corrected.Stacks}:{corrected.Qty}";
            ApplyRate(corrected);
            Changed?.Invoke();
            return $"hover corrected item to {hovered.name}";
        }
        if (cards.FirstOrDefault(c => c.Key == _hoverDone) is { } done) return $"read the stats of {done.Name}";
        if (cards[0].IsScroll)
        {
            var scroll = cards[0];
            var (stats, whyNot) = await ScrollReader.ReadWhyAsync(_ctx.Ocr, frame, scroll.Name, cursor);
            if (stats == null) return $"hovering {scroll.Name}: no details yet ({whyNot})" + HoverFailed(scroll.Key, scroll.Name, frame, cursor, lay, whyNot);
            _hoverDone = scroll.Key;
            HoverRead(scroll.Key);
            return PutScrollStats(scroll, stats);
        }
        if (!cards[0].IsEquipment) return $"hovering {cards[0].Name}";
        string why = "";
        foreach (var card in cards)   // normally one; older cards for the same slot are tried too
        {
            var (details, reason) = await EquipmentReader.ReadWhyAsync(_ctx.Ocr, frame, card.Name, both => ChooseHovered(card, both));
            // The worn item's tooltip beside this one (a different item) tells what is worn, for when its twin is hovered.
            await LearnWorn(frame, card.Name);
            if (details == null) { why = reason; continue; }
            _hoverDone = card.Key;
            HoverRead(card.Key);
            return PutStats(card, details);
        }
        return $"hovering {cards[0].Name}: no stats yet ({why})" + HoverFailed(cards[0].Key, cards[0].Name, frame, cursor, lay, why);
    }

    /// <summary>
    /// Puts a tooltip reading on a card. Stats you typed or corrected yourself are never replaced, and a reading that
    /// has fewer stat lines than the card already holds is taken as an incomplete read.
    /// </summary>
    string PutStats(SaleItem card, EquipmentDetails details)
    {
        bool mine = (!string.IsNullOrWhiteSpace(card.Stats) && card.Stats != card.AutoStats) ||
                    (!string.IsNullOrWhiteSpace(card.Slots) && card.Slots != card.AutoSlots);
        if (mine) return $"{card.Name}: kept the stats you entered";
        if (!EquipmentReader.CoversRecordedStats(card.Stats, details.Stats))
            return $"{card.Name}: incomplete tooltip read; kept previous stats, hover again";
        if (card.Stats == details.Stats && card.Slots == details.Slots) return $"read the stats of {card.Name}";
        card.AutoStats = details.Stats; card.AutoSlots = details.Slots;
        card.Stats = details.Stats;
        card.Slots = details.Slots;
        SaveSession();
        return $"read the stats of {card.Name}";
    }

    // ---------------------------------------------------------------- worn equipment

    /// <summary>
    /// The stats of items you are wearing, by item name, as seen in the comparison tooltip the game shows beside a
    /// hovered item of the same kind. Kept for this run only: you may change what you wear.
    /// </summary>
    readonly Dictionary<string, string> _worn = new(StringComparer.OrdinalIgnoreCase);

    static string NameKey(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToUpperInvariant(), "[^A-Z0-9]", "");

    /// <summary>When two tooltips of different items are showing, the one not hovered is what you wear: remember its stats.</summary>
    async Task LearnWorn(Frame frame, string hovered)
    {
        var all = await EquipmentReader.AllAsync(_ctx.Ocr, frame);
        if (all.Count < 2) return;
        foreach (var (title, details) in all)
        {
            var name = System.Text.RegularExpressions.Regex.Replace(title, @"\s*\(\+\d+\)\s*$", "");
            if (NameKey(name) == NameKey(hovered)) continue;
            _worn[NameKey(name)] = EquipmentReader.Profile(details.Stats, details.Slots);
        }
    }

    /// <summary>
    /// Two tooltips of the hovered item: yours and the identical item you wear. The one whose stats are what this card
    /// already holds is yours; otherwise, if exactly one matches what you are known to wear, the other is yours.
    /// With nothing to go on, nothing is picked (stats are never guessed).
    /// </summary>
    EquipmentDetails? ChooseHovered(SaleItem card, IReadOnlyList<EquipmentDetails> both)
    {
        if (!string.IsNullOrWhiteSpace(card.Stats))
        {
            var mine = both.Where(d => EquipmentReader.Profile(d.Stats, d.Slots) == EquipmentReader.Profile(card.Stats, card.Slots)).ToList();
            if (mine.Count == 1) return mine[0];
        }
        return NotWorn(card.Name, both);
    }

    /// <summary>Of two tooltips of one item, the one that isn't what you are known to wear; null when that can't be told.</summary>
    EquipmentDetails? NotWorn(string name, IReadOnlyList<EquipmentDetails> both)
    {
        if (!_worn.TryGetValue(NameKey(name), out var worn)) return null;
        var notWorn = both.Where(d => EquipmentReader.Profile(d.Stats, d.Slots) != worn).ToList();
        return notWorn.Count == 1 && notWorn.Count < both.Count ? notWorn[0] : null;
    }

    /// <summary>Puts a scroll's success rate and effect on its card, unless you typed your own there.</summary>
    string PutScrollStats(SaleItem card, string stats)
    {
        if (!string.IsNullOrWhiteSpace(card.Stats) && card.Stats != card.AutoStats) return $"{card.Name}: kept the details you entered";
        if (card.Stats == stats) return $"read {card.Name}";
        card.AutoStats = stats;
        card.Stats = stats;
        SaveSession();
        return $"read {card.Name}: {stats.Replace("\n", ", ")}";
    }

    // ---------------------------------------------------------------- hover checks

    readonly Dictionary<string, int> _hoverMisses = new();
    readonly HashSet<string> _hoverChecked = new();

    void HoverRead(string key) => _hoverMisses.Remove(key);

    /// <summary>
    /// Counts tooltip reads that came to nothing while the same card was hovered. On the third in a row, saves the
    /// game picture and what was tried to the diagnostics folder (once per card per run), so the miss can be looked
    /// at later without the game. Returns a note for the status line.
    /// </summary>
    string HoverFailed(string key, string name, Frame frame, (int x, int y)? cursor, object? layout, string why)
    {
        int n = _hoverMisses[key] = _hoverMisses.GetValueOrDefault(key) + 1;
        if (n < 3 || !_hoverChecked.Add(key)) return "";
        try
        {
            var folder = Path.Combine(_folder, "diagnostics");
            Directory.CreateDirectory(folder);
            foreach (var old in new DirectoryInfo(folder).GetFiles("hover-*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(20)) old.Delete();
            var stem = Path.Combine(folder, $"hover-{DateTime.Now:yyyyMMdd-HHmmss}");
            FrameFiles.SavePng(frame, stem + ".png");
            File.WriteAllText(stem + ".txt",
                $"card: {name}\nkey: {key}\nwhy: {why}\ncursor: {cursor?.ToString() ?? "unknown"}\nlayout: {layout}\nframe: {frame.W}x{frame.H}\nocr: {_ctx.Ocr.EngineName}\n");
            return " · saved a hover check to the diagnostics folder";
        }
        catch (Exception e) { _ctx.Log("hover check not saved: " + e.Message); return ""; }
    }

    public string DiagnosticsFolder => Path.Combine(_folder, "diagnostics");

    /// <summary>
    /// Whether a name read off a tooltip may replace what the icon said. The icon is trusted for everything except
    /// what it cannot tell: a slot it did not recognise, and upgrade scrolls, which share icons. The name must also
    /// be the kind of item the open tab holds, so stray text near the pointer (a pet's name tag, chat) is ignored.
    /// </summary>
    static bool MayRename(int from, CatalogItem to, string tab)
    {
        int kind = tab switch { "Equip" => 1, "Use" => 2, "Set Up" => 3, "Etc" => 4, "Cash" => 5, _ => 0 };
        if (kind != 0 && to.id / 1000000 != kind) return false;
        return from == 0 || (from / 10000 == 204 && to.id / 10000 == 204);
    }

    /// <summary>Gives an equipment card with nothing on it the stats last read for that item, if they are not on another card.</summary>
    void Recall(SaleItem card)
    {
        if (!card.IsEquipment || card.IsWanted || card.ItemId == 0 || !string.IsNullOrWhiteSpace(card.Stats) || !string.IsNullOrWhiteSpace(card.Slots)) return;
        var inUse = Items.Where(i => i != card && i.ItemId == card.ItemId && !string.IsNullOrWhiteSpace(i.Stats)).Select(i => i.Stats + "|" + i.Slots).ToHashSet();
        if (_hover.Find(card.ItemId, card.Key, inUse) is not { } seen) return;
        // marked as read by the app, so hovering the item again still refreshes it
        card.AutoStats = seen.Stats; card.AutoSlots = seen.Slots;
        card.Stats = seen.Stats;
        card.Slots = seen.Slots;
    }

    // ---------------------------------------------------------------- scanning a grid

    readonly HashSet<string> _unsure = new();   // unrecognised slots seen on the last automatic scan
    bool _unsurePending;
    readonly HashSet<string> _missing = new();  // cards not found on the last read of their tab: removed if still gone on the next
    /// <summary>Per inventory tab, the slots the last scan saw empty. A card is never made for one of those from text near the pointer.</summary>
    readonly Dictionary<string, HashSet<(int, int)>> _emptySlots = new();
    /// <summary>Per slot, a fingerprint of its picture and the item read there at the last scan taken with the pointer away.</summary>
    readonly Dictionary<(string tab, int row, int col), (string hash, int id)> _slotSeen = new();
    /// <summary>Scrolls share one icon, so a scroll's name comes from its tooltip, kept per slot with the slot's picture.</summary>
    readonly Dictionary<(string tab, int row, int col), (string hash, int id)> _slotTitle = new();
    (string tab, int row, int col)? _flagged;   // the slot whose tooltip said it cannot be sold; named on the next scan
    string _flagSpot = ""; int _flagTries;

    static string SlotHash(SlotRead s) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Pixels.AsSpan())));

    /// <summary>Takes away every card of an item that is on the not-for-sale list.</summary>
    bool DropNotForSale()
    {
        var gone = Items.Where(i => _notForSale.Has(i.Ident)).ToList();
        foreach (var g in gone) Remove(g);
        return gone.Count > 0;
    }

    /// <summary>
    /// Adds the items of one grid (an inventory tab or the storage window). With auto on (the monitor), it stays quiet
    /// and only adds an unrecognised slot once it has looked the same twice in a row, and removes a card only when its
    /// item is still gone on a second look at a whole, uncovered tab.
    /// </summary>
    async Task<(int added, string note)> ScanGrid(Frame frame, InvLayout lay, string tab, bool auto)
    {
        var vision = Vision!;
        bool storage = tab == StorageTab;
        var slots = await Task.Run(() => vision.ReadSlots(frame, lay));
        if (!storage && lay.Tab >= 0)
        {
            var empty = new HashSet<(int, int)>();
            for (int r = 0; r < Vision.Rows; r++) for (int c = 0; c < Vision.Cols; c++) empty.Add((r, c));
            foreach (var s in slots) empty.Remove((s.Row, s.Col));
            _emptySlots[tab] = empty;
        }
        // Scroll icons are shared by different upgrades. An icon-only refresh cannot overturn a title already recorded for this slot.
        if (!storage && tab == "Use")
            foreach (var slot in slots.Where(s => s.ItemId / 10000 == 204))
                if (Items.FirstOrDefault(i => i.Tab == "Use" && i.ItemId / 10000 == 204 && i.SlotRow == slot.Row && i.SlotCol == slot.Col) is { } recorded)
                    slot.ItemId = recorded.ItemId;
        var cursorNow = Cursor();
        // Slot pictures are only trusted when the pointer is away from the grid (it is drawn over the slot it covers).
        if (!storage && !InGrid(lay, cursorNow))
            foreach (var slot in slots)
            {
                var k = (tab, slot.Row, slot.Col);
                string h = SlotHash(slot);
                if (_slotTitle.TryGetValue(k, out var t))
                {
                    if (t.hash != h) _slotTitle.Remove(k);              // something else is in this slot now
                    else if (slot.ItemId / 10000 == 204) slot.ItemId = t.id;
                }
                _slotSeen[k] = (h, slot.ItemId);
            }
        // A hovered tooltip contains the real title. Use it once before grouping so a shared scroll icon cannot become the wrong scroll card.
        CatalogItem? hoveredCorrection = null;
        int hoveredRow = -1, hoveredCol = -1;
        if (!storage && InGrid(lay, cursorNow) && await ItemTooltipReader.ReadAsync(_ctx.Ocr, frame, cursorNow, vision.Catalog.Values) is { } hovered)
        {
            var cursor = cursorNow!.Value;
            int row = (cursor.y - lay.GridY) / Vision.PitchY, col = (cursor.x - lay.GridX) / Vision.PitchX;
            if (slots.FirstOrDefault(s => s.Row == row && s.Col == col) is { } slot && MayRename(slot.ItemId, hovered, tab))
            {
                hoveredCorrection = hovered; hoveredRow = row; hoveredCol = col;
                slot.ItemId = hovered.id;
            }
        }
        var seenUnsure = new HashSet<string>();
        _unsurePending = false;
        // The slot whose tooltip said "quest item" / "one-of-a-kind" / "untradable": now that the pointer is off it,
        // what is in it goes on the not-for-sale list.
        if (!storage && _flagged is { } fl && fl.tab == tab)
        {
            if (slots.FirstOrDefault(s => s.Row == fl.row && s.Col == fl.col) is { } bad)
                _notForSale.Add(bad.ItemId != 0 ? "id:" + bad.ItemId : "pic:" + SlotHash(bad));
            _flagged = null;
            if (DropNotForSale()) Changed?.Invoke();
        }
        slots = slots.Where(s => !_notForSale.Has(s.ItemId != 0 ? "id:" + s.ItemId : "pic:" + SlotHash(s))).ToList();
        bool Equip(SlotRead s) => s.ItemId / 1000000 == 1 || tab == "Equip";
        // Keep user choices even when a refresh changes a stack key or temporarily removes a card before adding its replacement.
        var priorCards = Items.Where(i => i.Tab == tab || (!storage && i.Tab == "Items")).ToList();
        var priorUsed = new HashSet<SaleItem>();

        // One entry per card: (key, a slot showing it, how many identical stacks are in view).
        var cards = new List<(string key, SlotRead slot, int stacks)>();
        if (!storage)
        {
            // Identical stacks (same item, same count) become one card: "500 ×8". Equipment is one card per slot.
            foreach (var g in slots.GroupBy(s => Equip(s) ? $"equip:{s.Row}:{s.Col}" : s.ItemId != 0 ? $"{s.ItemId}:{s.Quantity}" : $"?{s.Row}:{s.Col}"))
            {
                var s = g.First();
                cards.Add((Equip(s) ? $"Equip:{s.Row}:{s.Col}:{s.ItemId}" : s.ItemId != 0 ? $"{tab}:{s.ItemId}:{g.Count()}:{s.Quantity}" : $"{tab}:?{SlotHash(s)}", s, g.Count()));
            }
        }
        else
        {
            // Storage scrolls, so a slot's position means nothing: cards go by item (and stack size), and the number
            // of copies is the most seen in one view.
            foreach (var g in slots.GroupBy(s => s.ItemId == 0 ? "?" + SlotHash(s) : Equip(s) ? "E" + s.ItemId : $"{s.ItemId}:{s.Quantity}"))
            {
                var list = g.ToList();
                if (list[0].ItemId != 0 && Equip(list[0]))
                    for (int i = 0; i < list.Count; i++) cards.Add(($"{tab}:E:{list[i].ItemId}:{i}", list[i], 1));
                else cards.Add(($"{tab}:{g.Key}", list[0], list.Count));
            }
        }

        int added = 0, unknown = 0, unclear = 0;
        var fresh = new List<SaleItem>();
        foreach (var (key, s, stacks) in cards)
        {
            bool carryInclude = true, carryState = false;
            string carryCustom = "", carryStats = "", carrySlots = "";
            void Carry(SaleItem stale)
            {
                if (!carryState) { carryInclude = stale.Include; carryCustom = stale.Custom; carryStats = stale.Stats; carrySlots = stale.Slots; carryState = true; }
                Remove(stale);
            }
            // A tooltip is authoritative for the slot under the cursor. Remove a stale card for the old icon/title
            // before looking up the new key; otherwise the wrong scroll survives beside the corrected one.
            if (hoveredCorrection != null && s.Row == hoveredRow && s.Col == hoveredCol)
                foreach (var stale in Items.Where(i =>
                    (i.Tab == tab || i.Tab == "Items" ||
                     (Categories.Of(i.ItemId) == Categories.Of(hoveredCorrection.id) && Categories.Of(i.ItemId) == "Upgrade scrolls")) &&
                    i.SlotRow == hoveredRow && i.SlotCol == hoveredCol && i.ItemId != hoveredCorrection.id).ToList())
                    Carry(stale);
            // "Items" is a legacy fallback tab. Once the active tab is known, don't keep its copy beside the real-tab card.
            if (!storage && tab != "Items" && s.ItemId != 0)
                foreach (var stale in Items.Where(i => i.Tab == "Items" && i.ItemId == s.ItemId &&
                    ((i.SlotRow == s.Row && i.SlotCol == s.Col) || i.Qty == (s.Quantity > 0 ? s.Quantity.ToString() : ""))).ToList())
                    Carry(stale);
            var existing = Items.FirstOrDefault(i => i.Key == key);
            if (existing != null)
            {
                if (storage && stacks > existing.Stacks) { existing.Stacks = stacks; existing.Qty = $"{s.Quantity} ×{stacks}"; }
                // Inventory items move between slots; the card follows, so a later scan (or a restart) puts each
                // look-alike scroll back on the right card.
                if (!storage && (existing.SlotRow != s.Row || existing.SlotCol != s.Col)) { existing.SlotRow = s.Row; existing.SlotCol = s.Col; }
                // A listing back from your shop whose item already had a card here (read before the shop was seen to close):
                // the card takes over the price and stats it doesn't have, and the listing goes, so the item isn't listed twice.
                if (!storage && s.ItemId != 0 && Items.FirstOrDefault(i => i.ItemId == s.ItemId && i.Key.StartsWith(ReturnedPrefix, StringComparison.Ordinal)) is { } back)
                    Absorb(existing, back);
                continue;
            }
            if (s.ItemId == 0 && (Vision.Offset(s.Pixels) || Vision.Blank(s.Pixels))) continue;   // a bad cut or an empty slot, not an item
            // The pointer's picture reaches about 30 pixels right and down from its tip: a slot it covers is not an
            // unknown item, just hidden. Wait until the pointer is elsewhere.
            if (s.ItemId == 0 && cursorNow is { } tip)
            {
                int sx = lay.GridX + s.Col * Vision.PitchX, sy = lay.GridY + s.Row * Vision.PitchY;
                if (tip.x + 34 >= sx && tip.x - 4 <= sx + Vision.PitchX && tip.y + 34 >= sy && tip.y - 4 <= sy + Vision.PitchY) { _unsurePending = true; unclear++; continue; }
            }
            if (auto && s.ItemId == 0)
            {
                // Could be a tooltip or a dragged item passing over the slot: wait for it to hold still.
                seenUnsure.Add(key);
                if (!_unsure.Contains(key)) { _unsurePending = true; unclear++; continue; }
            }

            var item = new SaleItem { Key = key, ItemId = s.ItemId, Tab = tab, Category = Categories.Of(s.ItemId), Stacks = stacks, SlotRow = s.Row, SlotCol = s.Col };
            var prior = priorCards.FirstOrDefault(i => !priorUsed.Contains(i) && i.ItemId == s.ItemId && i.SlotRow == s.Row && i.SlotCol == s.Col)
                ?? priorCards.FirstOrDefault(i => !priorUsed.Contains(i) && i.ItemId == s.ItemId && !i.IsEquipment && !item.IsEquipment)
                // a listing back from your shop: any copy of the item takes it over (equipment too, as it has no slot yet)
                ?? priorCards.FirstOrDefault(i => !priorUsed.Contains(i) && i.ItemId == s.ItemId && i.Key.StartsWith(ReturnedPrefix, StringComparison.Ordinal));
            if (prior != null)
            {
                priorUsed.Add(prior);
                carryState = true;
                carryInclude = prior.Include; carryCustom = prior.Custom; carryStats = prior.Stats; carrySlots = prior.Slots;
                item.AutoStats = prior.AutoStats; item.AutoSlots = prior.AutoSlots;
                // the card back from the shop has found its slot: this card replaces it
                if (prior.Key.StartsWith(ReturnedPrefix, StringComparison.Ordinal)) Remove(prior);
            }
            if (carryState)
            {
                item.Include = carryInclude; item.Custom = carryCustom; item.Stats = carryStats; item.Slots = carrySlots;
            }
            bool showQty = !item.IsEquipment && (s.HasCount || stacks > 1);
            item.Qty = !showQty ? "" : stacks > 1 ? $"{s.Quantity} ×{stacks}" : s.Quantity.ToString();
            if (s.ItemId != 0 && vision.Catalog.TryGetValue(s.ItemId, out var cat))
            {
                item.Name = cat.name;
                item.Url = ItemLinks.Kerning(cat);
                item.Icon = Icon(s.ItemId, s.Pixels);
                if (!carryState) item.Custom = item.IsEquipment ? "" : _book.Get(s.ItemId) ?? "";
                ApplyRate(item);
            }
            else
            {
                unknown++;
                item.Name = "Unknown item";
                item.IconPx = s.Pixels;
                item.Icon = Icon(0, s.Pixels);
                item.Include = false; // uncertain identity; this does not establish tradeability
            }
            if (!carryState && s.ItemId != 0)
            {
                if (S.GroupChoices.TryGetValue(item.Tab == StorageTab ? PlaceStorage : PlaceBag, out bool placeChoice)) item.Include = placeChoice;
                if (S.GroupChoices.TryGetValue(item.Category, out bool categoryChoice)) item.Include = categoryChoice;
            }
            Add(item);
            fresh.Add(item);
            added++;
        }

        // Keep the list matching the game: a card whose item is no longer in this tab goes away. Only when the
        // whole tab is showing (no scrolling), nothing looks covered, and it is still gone on a second look.
        var stillMissing = new HashSet<string>();
        bool whole = auto && unclear == 0 && (storage || lay.Tab >= 0) && !Vision.Scrollable(frame, lay);
        if (whole)
        {
            var present = cards.Select(k => k.key).ToHashSet();
            foreach (var gone in Items.Where(i => i.Tab == tab && !present.Contains(i.Key)).ToList())
            {
                if (!_missing.Contains(gone.Key)) { stillMissing.Add(gone.Key); _unsurePending = true; continue; }
                // The same item turning up in another slot or stack size keeps what you set on it. Failing that,
                // equipment you moved between inventory and storage: a bare card of the same item in the other place.
                static bool Bare(SaleItem n) => string.IsNullOrWhiteSpace(n.Stats) && string.IsNullOrWhiteSpace(n.Slots) && string.IsNullOrWhiteSpace(n.Custom);
                bool worth = !gone.IsEquipment || !Bare(gone);
                var heir = gone.ItemId == 0 ? null
                    : fresh.FirstOrDefault(n => n.ItemId == gone.ItemId)
                    ?? (gone.IsEquipment && worth ? Items.FirstOrDefault(n => n != gone && n.Tab == tab && present.Contains(n.Key) && n.ItemId == gone.ItemId && Bare(n)) : null)
                    ?? (gone.IsEquipment && worth ? Items.FirstOrDefault(n => n != gone && n.Tab != tab && n.ItemId == gone.ItemId && Bare(n)) : null);
                if (heir != null)
                {
                    fresh.Remove(heir);
                    // Inclusion was resolved when the replacement was created: a stale card never overwrites a newer choice.
                    if (heir.IsEquipment)
                    {
                        heir.Custom = gone.Custom; heir.Name = gone.Name;
                        heir.AutoStats = gone.AutoStats; heir.AutoSlots = gone.AutoSlots;
                        heir.Stats = gone.Stats; heir.Slots = gone.Slots;
                    }
                }
                Remove(gone);
            }
        }
        foreach (var card in Items.Where(i => i.Tab == tab).ToList()) Recall(card);
        _missing.RemoveWhere(k => k.StartsWith(StorageTab + ":") == storage);
        _missing.UnionWith(stillMissing);
        _unsure.RemoveWhere(k => k.StartsWith(StorageTab + ":") == storage);
        _unsure.UnionWith(seenUnsure);
        if (added > 0) { Changed?.Invoke(); SaveSession(); }
        string where = storage ? "Storage" : $"{tab} tab";
        return (added, added == 0 ? $"{where}: nothing new."
            : $"{where}: added {added} item{(added == 1 ? "" : "s")}" +
              (unknown > 0 ? $", {unknown} not recognised (left out; name them and tick them to include)." : "."));
    }

    // ---------------------------------------------------------------- game info

    /// <summary>Fills Location and IGN from the game picture. With overwrite off, only empty boxes are filled and nothing is reported.</summary>
    async Task ReadGameInfo(Frame frame, bool overwrite)
    {
        var notes = new List<string>();
        if (overwrite || string.IsNullOrWhiteSpace(S.Seller))
        {
            var ign = await IgnReader.ReadAsync(_ctx.Ocr, frame);
            if (!string.IsNullOrWhiteSpace(ign)) { S.Seller = S.AutoIgn = ign; notes.Add("IGN read."); }
            else notes.Add("Couldn't read the IGN from the status bar (is it covered?), so type it in.");
        }
        if (overwrite || string.IsNullOrWhiteSpace(S.Location))
        {
            var (state, text) = await MapReader.ReadAsync(_ctx.Ocr, frame);
            if (!string.IsNullOrWhiteSpace(text)) { S.Location = S.AutoLocation = text; notes.Add("Location read."); }
            else notes.Add(state switch
            {
                MinimapState.NameHidden => "The minimap is at its middle size, which hides the map name: click its - or + button (top-left of the game) so the name shows, then read again.",
                MinimapState.NotFound => "Can't see the minimap (hidden or covered): show it in the game, or type the location.",
                _ => "Couldn't read the map name, so type the location in.",
            });
        }
        var channel = ChannelReader.Read(frame);
        if (channel != null) { S.Channel = channel.ToString()!; notes.Add($"Channel {channel} read."); }
        else if (string.IsNullOrWhiteSpace(S.Channel)) notes.Add("For the channel: open the Change Channel window in the game and press this again, or type it in.");
        if (overwrite)
        {
            await ReadAvatar(frame);
            if (_avatarFor != S.Seller.Trim()) notes.Add("No picture of your character yet: it is taken from where your name shows under it.");
            Say(string.Join(" ", notes));
        }
        Changed?.Invoke();
        CardChanged?.Invoke();
        SaveSession();
    }

    /// <summary>Read tooltip (5s): counts down, then reads this item's tooltip from the newest game picture.</summary>
    public async Task ReadEquipmentAsync(SaleItem item)
    {
        for (int seconds = 5; seconds > 0; seconds--)
        {
            Say($"Hover your {item.Name} in the game and keep its tooltip visible. Reading in {seconds}…");
            await Task.Delay(1000);
        }
        var frame = Latest();
        if (frame == null) return;
        if (item.IsScroll)
        {
            var (stats, why) = await ScrollReader.ReadWhyAsync(_ctx.Ocr, frame, item.Name, Cursor());
            if (stats == null) { Say($"Couldn't read {item.Name}: {why}. Try again or type its success rate and effect."); return; }
            item.AutoStats = stats;
            item.Stats = stats;
            Say($"Read {item.Name}: {stats.Replace("\n", ", ")}.");
            return;
        }
        var details = await EquipmentReader.ReadAsync(_ctx.Ocr, frame, item.Name);
        if (details == null) { Say("Couldn't read one unambiguous tooltip for this item. Try again or enter its stats and remaining slots."); return; }
        item.Stats = details.Stats;
        item.Slots = details.Slots;
        Say($"Read {item.Name}. Review the stats, signs and remaining slots before exporting.");
    }

    // ---------------------------------------------------------------- prices

    /// <summary>A card's going rate: the average of every ticked source that has a price for the item.</summary>
    public void ApplyRate(SaleItem item)
    {
        var parts = new List<(string label, long price)>();
        bool exactFm = false; int seen = 0;
        if (!S.NoFm)
        {
            var exact = item.IsEquipment && item.HasDetails ? _fm.Rate(item.ItemId, item.Stats, item.Slots) : null;
            var rate = exact ?? (item.ItemId != 0 ? _fm.Rate(item.ItemId) : null);
            if (rate is { } r) { parts.Add(("FM", r.price)); exactFm = exact != null; seen = r.seen; }
        }
        int figure = S.SiteFigure;
        foreach (var site in PriceSites.All)
            if (S.SourceOn(site) && item.ItemId != 0 && _site.Get(site, item.ItemId)?.Pick(figure) is > 0 and var v)
                parts.Add((site, v));

        item.RateParts = parts.ToArray();   // before Rate, whose change tells the rows to redraw their buttons
        item.FromSite = parts.Any(p => p.label != "FM");
        if (parts.Count == 0) { item.RateValue = null; item.RateNote = ""; item.Rate = ""; return; }
        item.RateValue = (long)Math.Round(parts.Average(p => (double)p.price));
        string match = item.IsEquipment ? exactFm && !item.FromSite ? "matching stats/slots" : "stats/slots not matched" : "";
        if (parts.Count == 1 && !item.FromSite)
        {
            item.RateNote = item.IsEquipment ? "FM ask · " + match : "";
            item.Rate = $"FM asking: {Mesos.Format(parts[0].price)} ({seen} seen)" + (match.Length > 0 ? " · " + match : "");
        }
        else if (parts.Count == 1)
        {
            item.RateNote = item.IsEquipment ? parts[0].label + " · " + match : "";
            item.Rate = $"{parts[0].label}: {Mesos.Format(parts[0].price)}";
        }
        else
        {
            item.RateNote = item.IsEquipment ? "average · " + match : "";
            item.Rate = $"Average {Mesos.Format(item.RateValue.Value)}: " + string.Join(" · ", parts.Select(p => $"{p.label} {Mesos.Format(p.price)}"));
        }
    }

    /// <summary>Records the prices of a player shop that is open in the game. Null when there is none.</summary>
    async Task<string?> WatchShop(Frame frame)
    {
        var vision = Vision!;
        var cursor = Cursor();
        var shop = await _shops.ReadAsync(_ctx.Ocr, vision, frame);
        // A tooltip over the list can hide rows (often the hovered one's own price) or the whole panel edge. While the
        // pointer is still where the last full read put the list, keep using that read's rows to know what is hovered.
        bool fresh = _lastShop != null && DateTime.UtcNow - _lastShopAt < TimeSpan.FromSeconds(30);
        bool inLast = fresh && cursor is { } pc && pc.x >= _lastShop!.Left && pc.x < _lastShop.Right;
        var closedNote = TrackOwnShop(shop, inLast);
        if (closedNote != null) return closedNote;
        if (shop == null && !inLast) return null;
        int owned = 0, added = 0, unknown = 0;
        string hoverNote = "";
        if (shop != null)
        {
            owned = ImportOwnShop(shop);
            foreach (var row in shop.Unchanged ? Enumerable.Empty<ShopRow>() : shop.Rows)
            {
                if (row.ItemId == 0 || row.Score < 0.72) { unknown++; continue; } // the row's name already agreed with the icon
                if (_fm.Add(row.ItemId, row.Price, row.Bundle, shop.Seller)) added++;
            }
            // Keep the read with the most rows for this shop as the map of where each listing is.
            if (!inLast || shop.Rows.Count >= _lastShop!.Rows.Count || shop.Seller != _lastShop.Seller) { _lastShop = shop; _lastShopAt = DateTime.UtcNow; }
        }
        var map = _lastShop ?? shop!;
        // Hovering a shop row reveals the stats that its icon and price cannot tell.
        if (cursor is { } c && c.x >= map.Left && c.x < map.Right)
        {
            static bool Readable(ShopRow r) => r.ItemId / 1000000 == 1 || r.ItemId / 10000 is 204 or 249;
            var hovered = (shop?.Rows ?? map.Rows).FirstOrDefault(r => c.y >= r.Top && c.y < r.Top + 36 && Readable(r))
                          ?? map.Rows.FirstOrDefault(r => c.y >= r.Top && c.y < r.Top + 36 && Readable(r));
            if (hovered != null && hovered.ItemId / 1000000 != 1 && vision.Catalog.TryGetValue(hovered.ItemId, out var scrollCat))
            {
                // A scroll in your own shop: its success rate and effect go on its card(s). Identical scrolls read the same.
                var (stats, why) = await ScrollReader.ReadWhyAsync(_ctx.Ocr, frame, scrollCat.name, cursor);
                string key = $"shop:{map.Seller}:{hovered.ItemId}:{hovered.Bundle}:{hovered.Price}";
                if (stats == null) hoverNote = map.IsOwnerView ? $"hovering {scrollCat.name}: no details yet ({why})" + HoverFailed(key, scrollCat.name, frame, cursor, $"shop {map.Left}..{map.Right}, row top {hovered.Top}", why) : "";
                else
                {
                    HoverRead(key);
                    var prefix = $"{ShopTab}:{map.Seller.ToUpperInvariant()}:{hovered.ItemId}:{hovered.Bundle}:{hovered.Price}:";
                    var notes = Items.Where(i => i.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList().Select(card => PutScrollStats(card, stats)).ToList();
                    hoverNote = notes.FirstOrDefault() ?? $"read {scrollCat.name}";
                }
            }
            else if (hovered != null && vision.Catalog.TryGetValue(hovered.ItemId, out var cat))
            {
                var (details, why) = await EquipmentReader.ReadWhyAsync(_ctx.Ocr, frame, cat.name, both => NotWorn(cat.name, both));
                await LearnWorn(frame, cat.name);
                string key = $"shop:{map.Seller}:{hovered.ItemId}:{hovered.Bundle}:{hovered.Price}";
                if (details != null)
                {
                    HoverRead(key);
                    if (_fm.Add(hovered.ItemId, hovered.Price, hovered.Bundle, map.Seller, details.Stats, details.Slots)) added++;
                    hoverNote = map.IsOwnerView && OwnShopCard(map.Seller, hovered, details) is { } card ? PutStats(card, details) : $"read the stats of {cat.name}";
                }
                else hoverNote = $"hovering {cat.name}: no stats yet ({why})" + HoverFailed(key, cat.name, frame, cursor, $"shop {map.Left}..{map.Right}, row top {hovered.Top}", why);
            }
        }
        if (added > 0)
        {
            _fm.Save();
            foreach (var it in Items) ApplyRate(it);
            CardChanged?.Invoke();
        }
        if (shop == null) return hoverNote.Length > 0 ? hoverNote : null;
        // The list shows six listings at a time; a longer shop scrolls, and every listing seen while scrolling is kept.
        bool full = shop.Rows.Count + shop.Sold.Count + shop.SoldUnread >= 6;
        return (shop.IsOwnerView ? $"my FM shop: {owned} new items, {shop.Sold.Count + shop.SoldUnread} sold" + (full ? " (scroll the shop list to pick up any listings below)" : "") + " · " : "")
               + $"shop: {shop.Rows.Count}/{shop.RowsSeen} prices read, {added} new" +
               (unknown > 0 ? $", {unknown} not recognised" : "") +
               (shop.Rows.Count < shop.RowsSeen ? " (unclear rows skipped)" : "") +
               (hoverNote.Length > 0 ? " · " + hoverNote : "");
    }

    ShopRead? _lastShop;
    DateTime _lastShopAt;
    int _shopGone;   // reads in a row without your own shop, since it was last seen open

    /// <summary>
    /// Notices your own shop closing: it was open (the owner's view, with Close Store), and then hasn't been seen for
    /// four reads in a row. Reads while the pointer is over where the list was don't count (a tooltip can hide it).
    /// When it closes, the listings that didn't sell go back to the inventory. Returns a status note when that happens.
    /// </summary>
    string? TrackOwnShop(ShopRead? shop, bool pointerOnList)
    {
        if (shop?.IsOwnerView == true)
        {
            _shopGone = 0;
            if (!S.ShopOpen) { S.ShopOpen = true; QueueSave(); }
            return null;
        }
        if (!S.ShopOpen || pointerOnList) return null;
        if (++_shopGone < 4) return null;
        _shopGone = 0;
        S.ShopOpen = false;
        _lastShop = null;
        int moved = ReturnShopItems();
        return moved > 0
            ? $"Your Free Market shop closed: {moved} unsold listing{(moved == 1 ? "" : "s")} moved back to the inventory (prices and stats kept)"
            : "Your Free Market shop closed";
    }

    /// <summary>
    /// Moves the My FM shop cards (listings that didn't sell) back to the inventory tab of their item, keeping price,
    /// stats and whether they're on the card. They have no slot until the inventory is next read; then the card for
    /// the slot the item is in takes over what they hold, and they go.
    /// </summary>
    internal int ReturnShopItems()
    {
        int moved = 0;
        foreach (var card in Items.Where(i => i.Tab == ShopTab).ToList())
        {
            card.Tab = TabOf(new SaleItem { ItemId = card.ItemId });
            card.Key = ReturnedPrefix + card.Key;
            card.SlotRow = card.SlotCol = -1;
            moved++;
            // the item already has a card in its tab (read while the shop was closing): that card takes it over now
            if (Items.FirstOrDefault(i => i != card && i.Tab == card.Tab && i.ItemId == card.ItemId && !i.Key.StartsWith(ReturnedPrefix, StringComparison.Ordinal)) is { } home) Absorb(home, card);
        }
        if (moved > 0) { SaveSession(); Changed?.Invoke(); CardChanged?.Invoke(); }
        return moved;
    }

    /// <summary>The Settings button for a shop closed while MapleLite wasn't watching: moves its listings back now.</summary>
    public void ShopClosedByHand()
    {
        S.ShopOpen = false; _shopGone = 0; _lastShop = null;
        int moved = ReturnShopItems();
        Say(moved > 0 ? $"Moved {moved} shop listing{(moved == 1 ? "" : "s")} back to the inventory (prices and stats kept)." : "No shop listings to move back.");
    }

    /// <summary>
    /// A listing back from your shop meets the inventory card of the same item: the card takes over the price, stats and
    /// "on the card" choice it doesn't have of its own, and the listing goes, so the item isn't listed twice.
    /// </summary>
    void Absorb(SaleItem home, SaleItem back)
    {
        if (string.IsNullOrWhiteSpace(home.Custom)) home.Custom = back.Custom;
        if (string.IsNullOrWhiteSpace(home.Stats) && string.IsNullOrWhiteSpace(home.Slots))
        { home.Stats = back.Stats; home.Slots = back.Slots; home.AutoStats = back.AutoStats; home.AutoSlots = back.AutoSlots; }
        if (back.Include) home.Include = true;
        Remove(back);
    }

    /// <summary>Key prefix of a card moved back from your shop and not yet matched to an inventory slot.</summary>
    public const string ReturnedPrefix = "Returned:";

    /// <summary>
    /// Takes sold listings (greyed out in your own shop) off the My FM shop cards. The n-th sold row of one listing
    /// (same item, bundle and price) removes one card, once: what was removed is remembered in the session, so seeing
    /// the same sold row again, even after a restart, never removes a second identical listing that is still for sale.
    /// </summary>
    void RemoveSold(ShopRead shop)
    {
        string seller = shop.Seller.ToUpperInvariant();
        var counts = new Dictionary<string, int>();
        bool changed = false;
        foreach (var sold in shop.Sold)
        {
            // which card this was: the item the name stands for, among this seller's cards with that bundle and price
            // card keys are "My FM shop:SELLER:item:bundle:price:copy"
            var cards = Items.Where(i => i.Tab == ShopTab && i.Key.Split(':') is { Length: 6 } k && k[1] == seller
                    && k[3] == sold.Bundle.ToString() && k[4] == sold.Price.ToString()
                    && (sold.ItemIds.Contains(i.ItemId) || (sold.ItemIds.Count == 0 && NameStarts(i.Name, sold.Name))))
                .OrderByDescending(i => i.Key, StringComparer.Ordinal).ToList();
            int id = cards.Select(c => c.ItemId).Distinct().Count() == 1 ? cards[0].ItemId : sold.ItemIds.Count == 1 ? sold.ItemIds[0] : 0;
            if (id == 0) continue;   // can't tell which item sold: leave the cards alone
            var identity = $"{seller}:{id}:{sold.Bundle}:{sold.Price}";
            int nth = counts[identity] = counts.GetValueOrDefault(identity) + 1;
            var mark = $"{identity}:{nth}";
            if (S.ShopSold.Contains(mark)) continue;
            S.ShopSold.Add(mark);
            changed = true;
            if (cards.FirstOrDefault(c => c.ItemId == id) is { } gone) Remove(gone);
        }
        if (changed) { SaveSession(); Changed?.Invoke(); CardChanged?.Invoke(); }
    }

    static bool NameStarts(string full, string shown)
    {
        static string K(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToUpperInvariant(), "[^A-Z0-9]", "");
        string a = K(full), b = K(shown);
        return b.Length >= 6 && (a.StartsWith(b, StringComparison.Ordinal) || a.StartsWith(b[..^1], StringComparison.Ordinal));
    }

    /// <summary>
    /// The My FM shop card for a hovered listing of your own shop: of the cards for that listing (same item, bundle and
    /// price), one already holding these stats, else the first without stats. Two identical listings with different
    /// rolls each get their own card as you hover them in turn.
    /// </summary>
    SaleItem? OwnShopCard(string seller, ShopRow row, EquipmentDetails details)
    {
        var prefix = $"{ShopTab}:{seller.ToUpperInvariant()}:{row.ItemId}:{row.Bundle}:{row.Price}:";
        var cards = Items.Where(i => i.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        string profile = EquipmentReader.Profile(details.Stats, details.Slots);
        return cards.FirstOrDefault(i => EquipmentReader.Profile(i.Stats, i.Slots) == profile)
               ?? cards.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Stats) && string.IsNullOrWhiteSpace(i.Slots))
               ?? (cards.Count == 1 ? cards[0] : null);
    }

    // Retain observed listings when the shop is scrolled, covered or closed.
    // Only the owner view may contribute items; other shops contribute prices.
    internal int ImportOwnShop(ShopRead shop)
    {
        if (!shop.IsOwnerView || Vision == null || string.IsNullOrWhiteSpace(shop.Seller)) return 0;
        RemoveSold(shop);
        int added = 0;
        var occurrences = new Dictionary<string, int>();
        foreach (var row in shop.Rows.Where(r => r.ItemId != 0 && r.Score >= 0.72 && r.Bundle > 0 && r.Price > 0))
        {
            if (!Vision.Catalog.TryGetValue(row.ItemId, out var cat)) continue;
            var identity = $"{shop.Seller.ToUpperInvariant()}:{row.ItemId}:{row.Bundle}:{row.Price}";
            int copy = occurrences.GetValueOrDefault(identity);
            occurrences[identity] = copy + 1;
            var key = $"{ShopTab}:{identity}:{copy}";
            // a listing moved back to the inventory when the shop looked closed, seen in the open shop again: it's a listing again
            if (Items.FirstOrDefault(i => i.Key == ReturnedPrefix + key) is { } back) { back.Key = key; back.Tab = ShopTab; back.SlotRow = back.SlotCol = -1; added++; continue; }
            if (Items.Any(i => i.Key == key)) continue;
            var item = new SaleItem { Key = key, ItemId = row.ItemId, Tab = ShopTab,
                Name = cat.name, Category = Categories.Of(row.ItemId), Icon = Icon(row.ItemId, null),
                Url = ItemLinks.Kerning(cat), Qty = row.Bundle > 1 ? row.Bundle.ToString() : "",
                Custom = SoldBySet(row.ItemId) ? row.Price.ToString() : row.Price % row.Bundle == 0
                    ? (row.Price / row.Bundle).ToString() : $"{row.Price:N0} per {row.Bundle}" };
            if (S.GroupChoices.TryGetValue(PlaceShop, out bool place)) item.Include = place;
            if (S.GroupChoices.TryGetValue(item.Category, out bool kind)) item.Include = kind;
            ApplyRate(item);
            Add(item);
            added++;
        }
        if (added > 0) { SaveSession(); Changed?.Invoke(); CardChanged?.Invoke(); }
        return added;
    }

    bool _browserBusy;

    async Task BrowserTickAsync()
    {
        if (_browserBusy || Vision == null || _browser == null || !S.BrowserTabs) return;
        _browserBusy = true;
        try
        {
            var (pages, note) = await _browser.ReadAsync(CancellationToken.None);
            if (!S.BrowserTabs) return;
            if (pages.Count == 0) { BrowserStatus = note; Changed?.Invoke(); return; }
            var notes = new List<string>();
            bool anyChanged = false;
            foreach (var page in pages)
            {
                int matched = 0, changed = 0; string? single = null;
                foreach (var read in page.Reads)
                {
                    int id = ResolveRead(read);
                    if (id == 0) continue;
                    matched++;
                    if (_site.Set(page.Site, id, read))
                    {
                        changed++;
                        foreach (var i in Items.Where(i => i.ItemId == id)) ApplyRate(i);
                    }
                    if (read.Last > 0 || read.Avg3 > 0 || read.Median10 > 0)
                        single = Vision.Name(id) + ":" + (read.Typical > 0 ? " usual " + Mesos.Format(read.Typical) : "") + (read.Last > 0 ? " last " + Mesos.Format(read.Last) : "")
                            + (read.Avg3 > 0 ? " 3 avg " + Mesos.Format(read.Avg3) : "") + (read.Median10 > 0 ? " median sold " + Mesos.Format(read.Median10) : "");
                }
                if (changed > 0) anyChanged = true;
                notes.Add($"{page.Site}: " + (page.Reads.Count == 0 ? page.Note : single ?? $"{matched} of {page.Reads.Count} prices matched"));
            }
            if (anyChanged) { _site.Save(); CardChanged?.Invoke(); }
            BrowserStatus = string.Join("  ·  ", notes);
            Changed?.Invoke();
        }
        catch (Exception ex) { BrowserStatus = "browser read failed: " + ex.Message; Changed?.Invoke(); }
        finally { _browserBusy = false; }
    }

    /// <summary>The catalog item a name read off a site stands for, or 0 when it is unknown or several items share it.</summary>
    int ResolveName(string name, bool truncated)
    {
        if (Vision == null) return 0;
        static string Key(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToUpperInvariant(), "[^A-Z0-9]", "");
        string key = Key(name);
        if (key.Length < 3) return 0;
        List<int> ids;
        if (truncated)
        {
            // a name cut off by its column: only safe when exactly one item starts that way
            if (key.Length < 8) return 0;
            ids = Vision.Catalog.Values.Where(c => Key(c.name).StartsWith(key, StringComparison.Ordinal)).Select(c => c.id).ToList();
            // the cut can leave a sliver of the next letter, read as a stray character
            if (ids.Count == 0 && key.Length > 9) ids = Vision.Catalog.Values.Where(c => Key(c.name).StartsWith(key[..^1], StringComparison.Ordinal)).Select(c => c.id).ToList();
        }
        else
        {
            ids = Vision.Catalog.Values.Where(c => Key(c.name) == key).Select(c => c.id).ToList();
            if (ids.Count == 0 && key.Length >= 6)
                ids = Vision.Catalog.Values.Where(c => ShopReader.Distance(Key(c.name), key) <= (key.Length >= 12 ? 2 : 1)).Select(c => c.id).ToList();
        }
        if (ids.Count > 1) ids = ids.Where(id => Items.Any(i => i.ItemId == id)).ToList();   // same name, several items: the one you have
        return ids.Count == 1 ? ids[0] : 0;
    }

    /// <summary>A list row gives the name's lines and sometimes a category line under it: try the whole, then fewer lines.</summary>
    int ResolveRead(SiteRead read)
    {
        for (int n = read.Names.Length; n >= 1; n--)
            if (ResolveName(string.Join(" ", read.Names.Take(n)), read.Truncated && n == 1 || read.Truncated && read.Names.Length == n) is > 0 and var id) return id;
        // a list that leaves the grade out of the name ("Shoes Speed Scroll" for each of its grades): the row's icon tells which
        if (Vision != null && !read.Truncated && read.Names.Length > 0) return Vision.ByIcon(read.Names[0], read.Icon);
        return 0;
    }

    // ---------------------------------------------------------------- the card

    /// <summary>The inventory tab an item belongs to: the one it was scanned from, or by its item id (storage, shops).</summary>
    public static string TabOf(SaleItem it) => Array.IndexOf(Vision.TabNames, it.Tab) >= 0 ? it.Tab : (it.ItemId / 1000000) switch
    {
        1 => "Equip", 2 => "Use", 3 => "Set Up", 4 => "Etc", 5 => "Cash", _ => "",
    };

    /// <summary>All ("") shows everything, Storage what was read from storage, the inventory tabs go by the item's type.</summary>
    /// <remarks>Buying cards belong only to the Buying list, never to a Selling tab (All included).</remarks>
    public static bool InTab(SaleItem it, string tab) => it.Tab == BuyTab ? tab == BuyTab
        : tab.Length == 0 || (tab == StorageTab || tab == ShopTab ? it.Tab == tab : TabOf(it) == tab);

    /// <summary>Throwing stars and bullets are sold by the set (one slot, recharged at a shop), not one by one.</summary>
    static bool SoldBySet(int id) => id / 10000 is 207 or 233;

    /// <summary>How many sets a star or bullet card stands for: "500 ×8" is eight, "611" is one.</summary>
    static long Sets(string qty)
    {
        var m = System.Text.RegularExpressions.Regex.Match(qty ?? "", @"[×x]\s*(\d+)\s*$");
        return m.Success ? long.Parse(m.Groups[1].Value) : 1;
    }

    /// <summary>How many items a card stands for: "12", "12 ×3" (three stacks of 12), or blank for one. Null if you typed something else.</summary>
    static long? Count(string qty)
    {
        if (string.IsNullOrWhiteSpace(qty)) return 1;
        var m = System.Text.RegularExpressions.Regex.Match(qty.Trim(), @"^(\d+)(?:\s*[×x]\s*(\d+))?$");
        return m.Success ? long.Parse(m.Groups[1].Value) * (m.Groups[2].Success ? long.Parse(m.Groups[2].Value) : 1) : null;
    }

    /// <summary>The card's tiles in the chosen order. Ties keep the order the items were found in.</summary>
    List<SaleItem> InOrder(List<SaleItem> chosen)
    {
        long Price(SaleItem i) => (!string.IsNullOrWhiteSpace(i.Custom) ? Mesos.Parse(i.Custom) : i.RateValue) ?? -1;
        int Kind(SaleItem i) { int k = Array.IndexOf(Categories.Order, i.Category); return k < 0 ? Categories.Order.Length : k; }
        return Math.Max(0, Array.IndexOf(Orders, S.Order)) switch
        {
            1 => chosen.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            2 => chosen.OrderByDescending(Price).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            3 => chosen,
            // the item id keeps a family together inside its kind (the ores, then the jewel ores, then the refined ones)
            _ => chosen.OrderBy(Kind).ThenBy(i => i.ItemId == 0 ? int.MaxValue : i.ItemId).ToList(),
        };
    }

    /// <summary>
    /// What the card shows now, and the card's options: the selling items ticked in the selected Selling tab, then the
    /// items you're buying, each group in the chosen order. Either group can be switched off the card as a whole.
    /// </summary>
    public (List<SaleItem> items, CardOptions options) Card()
    {
        var selling = S.HideSelling ? new List<SaleItem>() : Items.Where(i => i.Include && i.Tab != BuyTab && InTab(i, S.Tab)).ToList();
        var buying = S.HideBuying ? new List<SaleItem>() : Items.Where(i => i.Include && i.Tab == BuyTab).ToList();
        var chosen = selling.Concat(buying).ToList();
        // Total: price each × quantity (× sets for stars and bullets). Items with no price ("Offer") are counted apart.
        string total = "", totalNote = "";
        double priceScale = Math.Clamp(S.PricePercent, 1, 200) / 100.0;   // the price slider, on the total too
        if (!S.NoTotal && S.PricesShown)
        {
            long sum = 0; int priced = 0, offers = 0;
            // what you are selling: items marked Buying are not part of the total
            foreach (var i in chosen.Where(i => !i.Buying))
            {
                long? each = !string.IsNullOrWhiteSpace(i.Custom) ? Mesos.Parse(i.Custom) : i.RateValue;
                if (each is long p) { p = Math.Max(1, (long)Math.Round(p * priceScale, MidpointRounding.AwayFromZero)); sum += p * (i.IsEquipment ? 1 : SoldBySet(i.ItemId) ? Sets(i.Qty) : Count(i.Qty) ?? 1); priced++; } else offers++;
            }
            if (priced > 0)
            {
                total = Mesos.Format(sum) + (S.Obo ? " OBO" : "");
                totalNote = offers > 0 && !S.HideOffer ? $"+ {offers} by offer" : "";
            }
        }
        string credit = S.PricesShown && chosen.Any(i => i.FromSite && string.IsNullOrWhiteSpace(i.Custom))
            ? "prices: " + string.Join(", ", new[] { (PriceSites.Meso, "meso.watch"), (PriceSites.Cozy, "cozy.markets"), (PriceSites.Nia, "meowdb.com"), (PriceSites.Classic, "maplestory-classic.com") }
                .Where(s => S.SourceOn(s.Item1)).Select(s => s.Item2)) : "";
        var options = new CardOptions
        {
            Title = S.Title, Seller = S.Seller, Server = S.Server, Channel = S.Channel, Location = S.MapNameHidden ? "" : S.Location, Tagline = S.Tagline ?? "",
            Avatar = S.NoAvatar ? null : AvatarFor(S.Seller), Map = S.NoMap ? null : CardMap(), MapScale = Math.Clamp(S.MapScale, 40, 100) / 100.0,
            Prices = S.PricesShown, Obo = S.Obo, HideOffer = S.HideOffer, Compact = S.CompactTiles, Stats = !S.NoStats,
            Total = total, TotalNote = totalNote, Credit = credit, PriceScale = priceScale,
            Theme = Math.Max(0, Array.FindIndex(CardRenderer.Themes, t => t.Name == S.Theme)),
            ShowTime = !S.NoTime, Zone = S.LocalTime ? S.Zone : "",
            ShopMarker = !S.NoShopMarker, Discord = S.Discord ?? "", SellerMarker = !S.NoSellerMarker, Each = !S.NoEach, Pack = !S.NoPack, TradeTags = !S.NoTradeTags,
            MarkerColours = new CardRenderer.MarkerColours(S.MarkerRing ?? "#E5332A", S.MarkerTagColour ?? "#E5332A", S.MarkerText ?? "#FFFFFF"), MarkerTag = S.MarkerTag,
        };
        return (InOrder(selling).Concat(InOrder(buying)).ToList(), options);
    }

    // ---------------------------------------------------------------- buying

    /// <summary>The card for an item you're buying, if you ticked it on the Buying tab.</summary>
    public SaleItem? BuyCard(int itemId) => Items.FirstOrDefault(i => i.Tab == BuyTab && i.ItemId == itemId);

    /// <summary>Ticks or unticks an item on the Buying tab: makes or removes its card (BUYING on the card, its own qty and price).</summary>
    public void SetBuying(CatalogItem cat, bool on)
    {
        var card = BuyCard(cat.id);
        if (on && card == null)
        {
            card = new SaleItem
            {
                Key = "Buy:" + cat.id, ItemId = cat.id, Tab = BuyTab, Buying = true, Name = cat.name, Category = Categories.Of(cat.id),
                // no price of its own to start: the going rate shows until you type what you'll pay
                Url = ItemLinks.Kerning(cat), Icon = Icon(cat.id, null), Include = true,
            };
            ApplyRate(card);
            Add(card);
        }
        else if (!on && card != null) Remove(card);
        else return;
        SaveSession();
        Changed?.Invoke();
    }

    /// <summary>The item's picture for a list (the catalog icon), loaded once.</summary>
    public IImage? IconOf(int itemId) => Icon(itemId, null);

    /// <summary>
    /// Items marked Buying with the old per-item switch become Buying cards (the item, its price and quantity), and the
    /// scanned card goes back to selling. Runs once per load; nothing to do for lists made since.
    /// </summary>
    void MoveOldBuyingMarks()
    {
        foreach (var old in Items.Where(i => i.Buying && i.Tab != BuyTab).ToList())
        {
            old.Buying = false;
            if (old.ItemId == 0 || BuyCard(old.ItemId) != null) continue;
            Add(new SaleItem
            {
                Key = "Buy:" + old.ItemId, ItemId = old.ItemId, Tab = BuyTab, Buying = true, Name = old.Name, Category = old.Category,
                Url = old.Url, Icon = old.Icon, Include = old.Include, Custom = old.Custom, Qty = old.Qty,
            });
        }
    }

    /// <summary>Reminder when the card went out without a channel or location.</summary>
    public string MissingNote()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(S.Channel)) missing.Add("channel");
        if (string.IsNullOrWhiteSpace(S.Location)) missing.Add("location");
        return missing.Count == 0 ? "" : $"  Note: no {string.Join(" or ", missing)} on it. Fill the box{(missing.Count > 1 ? "es" : "")} in Card Design and export again if buyers need to find you.";
    }

    public void Report(string status) => Say(status);

    // ---------------------------------------------------------------- list actions

    public void SetIncluded(IEnumerable<SaleItem> items, bool include)
    {
        _bulk = true;
        foreach (var i in items.ToList()) i.Include = include;
        _bulk = false;
        Changed?.Invoke();
        SaveSession();
    }

    public void SetGroup(string group, bool include, IEnumerable<SaleItem> members)
    {
        S.GroupChoices[group] = include;
        SetIncluded(members, include);
    }

    public void Clear()
    {
        foreach (var i in Items.ToList()) Remove(i);
        _gridHash = 0; _storageHash = 0;
        Say("List cleared.");
        SaveSession();
        Changed?.Invoke();
    }

    /// <summary>
    /// Backs up the given saved files into Backups/reset-…, removes them, and starts afresh in place (the plugin keeps
    /// running; MapleLite doesn't restart).
    /// </summary>
    public string Reset(bool prices)
    {
        _fm.Save(); _site.Save();
        var files = prices ? DataFiles.All : DataFiles.Others;
        var backup = Path.Combine(_folder, "Backups", $"reset-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(backup);
        foreach (var f in files)
            if (File.Exists(Path.Combine(_folder, f))) File.Copy(Path.Combine(_folder, f), Path.Combine(backup, f), true);
        foreach (var f in files)
            foreach (var path in new[] { f, f + ".bak", f + ".tmp" })
                try { File.Delete(Path.Combine(_folder, path)); } catch { }
        ReloadAll();
        Say("Reset done. A copy of what was cleared is in " + backup);
        return backup;
    }

    /// <summary>
    /// Copies the Sale Card Generator app's saved files in from its Data folder (the app's files are never changed).
    /// What the plugin had is backed up first.
    /// </summary>
    public string Import(string appData)
    {
        var present = DataFiles.All.Where(f => File.Exists(Path.Combine(appData, f))).ToList();
        if (present.Count == 0) throw new FileNotFoundException("That folder has none of the Sale Card Generator's files (session.json, fm-prices.json, …). Pick its Data folder.");
        _fm.Save(); _site.Save(); SaveSession();
        var backup = Path.Combine(_folder, "Backups", $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(backup);
        foreach (var f in DataFiles.All)
            if (File.Exists(Path.Combine(_folder, f))) File.Copy(Path.Combine(_folder, f), Path.Combine(backup, f), true);
        foreach (var f in present) File.Copy(Path.Combine(appData, f), Path.Combine(_folder, f), true);
        ReloadAll();
        var note = $"Imported {present.Count} file{(present.Count == 1 ? "" : "s")} from the Sale Card Generator ({string.Join(", ", present)}).";
        Say(note);
        return note;
    }

    void ReloadAll()
    {
        bool monitoring = Monitoring;
        _watch?.Stop();
        _sessionReady = false;
        foreach (var i in Items.ToList()) Remove(i);
        (_book, _hover, _notForSale, _fm, _site) = (new(_folder), new(_folder), new(_folder), new(_folder), new(_folder));
        _mapPx = null; _avatarPx = null; AvatarShown = null; _avatarFor = ""; _portrait.Clear();
        _gridHash = 0; _storageHash = 0; _lastLay = null; _hoverDone = null;
        _slotSeen.Clear(); _slotTitle.Clear(); _missing.Clear(); _unsure.Clear(); _emptySlots.Clear();
        LoadSession();
        if (monitoring || !S.MonitorOff) SetMonitor(!S.MonitorOff);
        CardChanged?.Invoke();
    }

    /// <summary>Folders where the Sale Card Generator app usually keeps its Data, for the import picker to start in.</summary>
    public static IEnumerable<string> LikelyAppDataFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Path.Combine(home, "Documents"), Path.Combine(home, "Downloads") })
        {
            var guess = Path.Combine(root, "MapleStorySaleCardGenerator", "Data");
            if (File.Exists(Path.Combine(guess, DataFiles.Session))) yield return guess;
        }
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Market;
using MapleLite.Sdk;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.DpsSimulator;

internal sealed record PlannedGear(string Slot, int ItemId, string Stats, string RemainingEnhancements, bool Owned);
internal sealed class SimulatorData
{
    public CombatSettings Combat { get; set; } = new();
    public List<PlannedGear> Preview { get; set; } = [];
    public string SelectedMonster { get; set; } = "";
    public int[]? Portrait { get; set; }
    public string PortraitCharacter { get; set; } = "";
}

internal sealed class MonsterData
{
    public List<Monster> Cached { get; set; } = [];
    public List<Monster> Curated { get; set; } = [];
    public DateTimeOffset? RefreshedAt { get; set; }
}

internal sealed class DpsPanel : UserControl
{
    static readonly string[] Slots =
    [
        "Weapon", "Shield", "Projectile", "Hat", "Face", "Eye", "Earrings", "Top", "Bottom", "Overall", "Shoes",
        "Gloves", "Cape", "Ring 1", "Ring 2", "Ring 3", "Ring 4", "Pendant", "Belt", "Medal", "Shoulder", "Other",
    ];
    readonly IPluginContext _ctx;
    readonly CancellationToken _token;
    readonly SimulatorData _data;
    readonly MonsterData _monsters;
    EquippedLoadout _equipped;
    MarketLookup? _market;
    IDisposable? _marketSubscription;
    readonly Dictionary<int, Bitmap> _icons = new();
    readonly Dictionary<PlannedGear, Task<MarketQuote?>> _archivedQuotes = new();
    readonly TextBlock _status = Muted("");
    readonly TextBlock _monsterInfo = Muted("");
    readonly TextBlock _cost = Muted("");
    readonly Button _refreshPrices = new() { Content = "Refresh prices", IsVisible = false };
    readonly TextBlock _summary = Muted("");
    readonly TextBlock _currentDps = Big("—");
    readonly TextBlock _previewDps = Big("—");
    readonly TextBlock _currentDetails = Muted("");
    readonly TextBlock _previewDetails = Muted("");
    readonly Image _portrait = new() { Height = 110, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Image _previewPortrait = new() { Height = 110, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    readonly WrapPanel _currentIcons = new();
    readonly WrapPanel _previewIcons = new();
    readonly StackPanel _currentRows = new() { Spacing = 4 };
    readonly StackPanel _previewRows = new() { Spacing = 4 };
    readonly TextBox _character = new() { Watermark = "Character name", Width = 165 };
    readonly TextBox _gearSearch = new() { Watermark = "Search gear by name", Width = 220 };
    readonly ComboBox _gear = new() { MinWidth = 200 };
    readonly ComboBox _slot = new() { ItemsSource = Slots, SelectedIndex = 0, MinWidth = 115 };
    readonly TextBox _stats = new() { Watermark = "One stat per line, e.g. Weapon Attack: +47", AcceptsReturn = true, MinHeight = 66, TextWrapping = TextWrapping.Wrap };
    readonly TextBox _enhancements = new() { Width = 165, Watermark = "Slots remaining" };
    readonly CheckBox _owned = new() { Content = "Already own this copy" };
    readonly TextBlock _ownershipHint = Muted("");
    readonly TextBox _monsterSearch = new() { Watermark = "Search monsters", Width = 165 };
    readonly ComboBox _target = new() { MinWidth = 195 };
    readonly Button _targetSource = new() { Content = "Monster source ↗" };
    bool _busy;
    bool _loading = true;
    string _editingTarget = "";
    Bitmap? _importedPortrait;

    sealed record GearChoice(CatalogItem Item)
    {
        public override string ToString() => $"{Item.name} ({Item.id})";
    }

    public DpsPanel(IPluginContext ctx, CancellationToken token)
    {
        _ctx = ctx;
        _token = token;
        _data = ctx.Storage.Load<SimulatorData>("scenario");
        _monsters = ctx.Storage.Load<MonsterData>("monsters");
        _equipped = ctx.Events.Last<EquippedLoadout>() ?? LoadEquipment();
        _character.Text = _equipped.Character;
        _gearSearch.TextChanged += (_, _) => SearchGear();
        _gear.SelectionChanged += (_, _) =>
        {
            _stats.Text = "";
            _enhancements.Text = "";
            _owned.IsChecked = false;
            if (_gear.SelectedItem is GearChoice choice) _slot.SelectedItem = DefaultSlot(choice.Item.id);
            UpdateOwnershipHint();
        };
        _monsterSearch.TextChanged += (_, _) => SearchMonsters(_data.SelectedMonster);
        _target.SelectionChanged += (_, _) =>
        {
            if (_loading || _target.SelectedItem is not Monster mob) return;
            _data.SelectedMonster = mob.Key;
            Persist();
            UpdateResults();
        };
        _targetSource.Click += (_, _) => { if (_target.SelectedItem is Monster { Source.Length: > 0 } mob) OpenUrl(mob.Source); };
        _status.IsVisible = false;
        _summary.IsVisible = false;
        _refreshPrices.Click += (_, _) => { _archivedQuotes.Clear(); UpdateCost(); };
        ToolTip.SetTip(_slot, "Choose the slot for interchangeable copies, such as rings.");
        var comparison = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        comparison.Children.Add(Card(Stack(5, Title("Equipped"), _portrait, _currentIcons, _currentDps, _currentDetails)));
        var preview = Card(Stack(5, Title("Preview"), _previewPortrait, _previewIcons, _previewDps, _previewDetails, _cost, _refreshPrices));
        Grid.SetColumn(preview, 2);
        comparison.Children.Add(preview);

        var buttons = Wrap(
            ActionButton("Record hovered gear (5s)", RecordGearAsync, true),
            ActionButton("Capture current portrait (5s)", CapturePortraitAsync),
            ActionButton("Choose preview image…", ChoosePreviewImageAsync),
            Button("Switch character…", SwitchCharacter));
        var equipment = Card(Stack(6, Title("Loadout"),
            Wrap(_character, _slot), buttons,
            Muted("Open the in-game Equipment window. Start a capture, then hover one equipped item and keep its tooltip visible. Repeat for each slot. Review the OCR stats before comparing."),
            _currentRows));
        var gear = Card(Stack(6, Title("Plan a gear swap"),
            Wrap(_gearSearch, _gear), Wrap(ActionButton("Load wiki base stats", LoadGearStatsAsync), _owned), _ownershipHint,
            _stats, Wrap(_enhancements, Button("Preview swap", () => AssignGear(false), true),
                Button("Save to equipped slot", () => AssignGear(true)), Button("Reset preview", ResetPreview)),
            _previewRows));
        var target = Card(Stack(6, Title("Target monster"), Wrap(_monsterSearch, _target, _targetSource,
                ActionButton("Refresh from MapleClassic Wiki", RefreshMonstersAsync), Button("Wiki ↗", () => OpenUrl(ClassicWiki.MonstersUrl))),
            _monsterInfo, BuildMonsterEditor()));
        var content = Stack(12, _status, target, comparison, _summary, BuildCombatInputs(), equipment, gear);
        content.Margin = new Thickness(0, 0, 12, 12);
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        SearchGear();
        SearchMonsters(_data.SelectedMonster);
        _loading = false;
        RefreshLoadouts();
        AttachedToVisualTree += (_, _) =>
        {
            ConnectMarket(_ctx.Events.Last<MarketLookup>());
            _marketSubscription = _ctx.Events.Subscribe<MarketLookup>(ConnectMarket);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _marketSubscription?.Dispose();
            if (_market != null) _market.Changed -= UpdateResults;
            _market = null;
        };
    }

    EquippedLoadout LoadEquipment()
    {
        var path = Path.Combine(_ctx.Storage.Folder, EquippedLoadout.FileName + ".json");
        try
        {
            if (File.Exists(path) && System.Text.Json.JsonSerializer.Deserialize<EquippedLoadout>(File.ReadAllText(path))
                is { Items: not null, Character: not null } loadout)
                return loadout;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { _ctx.Log(ex.Message); }
        return new("", []);
    }

    void ConnectMarket(MarketLookup? market)
    {
        if (_market != null) _market.Changed -= UpdateResults;
        _market = market;
        if (_market != null) _market.Changed += UpdateResults;
        UpdateResults();
    }

    Button ActionButton(string label, Func<Task> action, bool accent = false)
    {
        var button = Button(label, () => { }, accent);
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception ex) { Status(ex.Message); }
            finally { _busy = false; button.IsEnabled = true; }
        };
        return button;
    }

    void Status(string message)
    {
        if (_token.IsCancellationRequested) return;
        _status.Text = message;
        _status.IsVisible = message.Length > 0;
        if (message.Length > 0) _ctx.Notify(message);
    }

    void Persist()
    {
        try { _ctx.Storage.Save("scenario", _data); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("Couldn't save simulator settings: " + ex.Message); }
    }

    void SaveEquipment(EquippedLoadout next)
    {
        _ctx.Storage.Save(EquippedLoadout.FileName, next);
        _equipped = next;
        _ctx.Events.Publish(next);
        RefreshLoadouts();
    }

    void SwitchCharacter()
    {
        string text = _character.Text?.Trim() ?? "";
        if (text.Length == 0 || text.Equals(_equipped.Character, StringComparison.OrdinalIgnoreCase))
        {
            Status("Enter a different character name to switch.");
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var confirm = new Window
        {
            Title = "Switch character", Width = 420, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
        };
        var body = Stack(12, Text($"Replace the equipped loadout and preview with a new loadout for {text}?"),
            Wrap(Button("Cancel", confirm.Close), Button("Switch", () =>
            {
                try
                {
                    SaveEquipment(new(text, []));
                    ResetPreview();
                    confirm.Close();
                }
                catch (IOException ex) { Status(ex.Message); }
            }, true)));
        body.Margin = new Thickness(16);
        confirm.Content = body;
        _ = confirm.ShowDialog(owner);
    }

    string CurrentName()
    {
        string typed = _character.Text?.Trim() ?? "";
        return typed.Length > 0 ? typed : _ctx.Game.Ign ?? "";
    }

    async Task RecordGearAsync()
    {
        if (!_ctx.Ocr.Available) { Status("An OCR engine is required to read a gear tooltip."); return; }
        string character = CurrentName();
        if (character.Length == 0) { Status("Enter your character name or wait for MapleLite to read it in game."); return; }
        if (_equipped.Character.Length > 0 && !character.Equals(_equipped.Character, StringComparison.OrdinalIgnoreCase))
        { Status("Use Switch character to start a different loadout."); return; }
        string selectedSlot = (string?)_slot.SelectedItem ?? "";
        Status("Hover a worn item in the Equipment window now and hold for five seconds.");
        await Task.Delay(TimeSpan.FromSeconds(5), _token);
        var frame = FreshFrame();
        var tooltips = await EquipmentReader.AllAsync(_ctx.Ocr, frame, requireComparison: false, knownTitle:
            title => _ctx.Items.ByName(title) is { } item && item.id / 1000000 == 1);
        _token.ThrowIfCancellationRequested();
        if (tooltips.Count != 1)
        { Status("Show exactly one worn item's tooltip. Close any comparison tooltip and try again."); return; }
        var (title, details) = tooltips[0];
        var catalog = _ctx.Items.Items.Where(i => DefaultSlot(i.id) != "Other").ToArray();
        var item = _ctx.Items.ByName(title) ?? ItemTooltipReader.Match(title, catalog)?.item;
        if (item == null) { Status($"Couldn't match the tooltip title “{title}”. Search for the item and save its stats manually."); return; }
        string slot = SlotOf(item.id, selectedSlot);
        if (_equipped.Items.FirstOrDefault(i => i.Slot == slot && i.ItemId == item.id) is { } prior
            && !EquipmentReader.CoversRecordedStats(prior.Stats, details.Stats))
        { Status($"The tooltip for {item.name} was incomplete. Your previous stats were kept."); return; }
        SetEquipped(character, new(slot, item.id, details.Stats, details.Slots));
        _character.Text = character;
        Status($"Recorded {item.name} in {slot}.");
    }

    Frame FreshFrame()
    {
        var latest = _ctx.Frames.Latest;
        return latest != null && DateTimeOffset.UtcNow - latest.Time < TimeSpan.FromSeconds(4)
            ? latest.Frame : throw new InvalidOperationException("Select the game on MapleLite's Game page and keep its window open.");
    }

    async Task CapturePortraitAsync()
    {
        string name = CurrentName();
        if (name.Length < 3) { Status("Enter your in-game name to capture the portrait."); return; }
        Status("Show your character and name in the game. Capturing in five seconds.");
        await Task.Delay(TimeSpan.FromSeconds(5), _token);
        var result = await AvatarReader.ReadAsync(_ctx.Ocr, FreshFrame(), name);
        _token.ThrowIfCancellationRequested();
        if (result is not { } found) { Status($"Could not find {name} in the game picture."); return; }
        _data.Portrait = found.px;
        _data.PortraitCharacter = name;
        Persist();
        RefreshLoadouts();
        Status("Captured current portrait.");
    }

    async Task ChoosePreviewImageAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var selection = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an outfit preview image", FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (selection.FirstOrDefault() is not { } file) return;
        await using var stream = await file.OpenReadAsync();
        _token.ThrowIfCancellationRequested();
        var bitmap = Bitmap.DecodeToWidth(stream, 240);
        var previous = _importedPortrait;
        _previewPortrait.Source = _importedPortrait = bitmap;
        _previewPortrait.IsVisible = true;
        previous?.Dispose();
        Status("Showing the supplied preview image.");
    }

    void SearchGear()
    {
        var terms = (_gearSearch.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _gear.ItemsSource = _ctx.Items.Items.Where(i => (i.id / 1000000 == 1 || DefaultSlot(i.id) == "Projectile")
                && terms.All(term => i.name.Contains(term, StringComparison.OrdinalIgnoreCase) || i.id.ToString().Contains(term)))
            .OrderBy(i => i.name).Take(80).Select(i => new GearChoice(i)).ToArray();
        if (terms.Length > 0 && _gear.ItemCount > 0) _gear.SelectedIndex = 0;
    }

    async Task LoadGearStatsAsync()
    {
        if (_gear.SelectedItem is not GearChoice choice) { Status("Choose a gear item first."); return; }
        string stats = await ClassicWiki.GearStatsAsync(choice.Item, _token);
        if (_gear.SelectedItem is not GearChoice current || current.Item.id != choice.Item.id) return;
        _stats.Text = stats;
        Status($"Loaded base stats for {choice.Item.name} from MapleClassic Wiki. Adjust them to match the roll you want.");
    }

    void AssignGear(bool current)
    {
        if (_gear.SelectedItem is not GearChoice choice) { Status("Search for and select a gear item first."); return; }
        if (string.IsNullOrWhiteSpace(_stats.Text) || !GearStats.TryParse(_stats.Text, out _))
        { Status("Enter the item’s stats as named lines, such as Weapon Attack: +47. Enter “None” for a statless item."); return; }
        string slots = _enhancements.Text?.Trim() ?? "";
        if (slots.Length > 0 && (!int.TryParse(slots, out int n) || n is < 0 or > 99))
        { Status("Enter 0–99 remaining slots, or leave the field blank."); return; }

        var item = choice.Item;
        string slot = SlotOf(item.id, (string?)_slot.SelectedItem ?? "");
        string stats = _stats.Text.Trim();
        if (current)
        {
            string name = CurrentName();
            if (name.Length == 0) { Status("Enter the character name first."); return; }
            if (_equipped.Character.Length > 0 && !name.Equals(_equipped.Character, StringComparison.OrdinalIgnoreCase))
            { Status("Use Switch character to start a different loadout."); return; }
            try { SetEquipped(name, new(slot, item.id, stats, slots)); }
            catch (IOException ex) { Status(ex.Message); return; }
            _character.Text = name;
        }
        else
        {
            _data.Preview.RemoveAll(p => Conflicts(p.Slot, p.ItemId, slot, item.id));
            _data.Preview.Add(new(slot, item.id, stats, slots, _owned.IsChecked == true));
            Persist();
            RefreshLoadouts();
        }
        Status($"Saved {item.name} to {slot} in the {(current ? "equipped loadout" : "preview")}.");
    }

    void SetEquipped(string character, WornItem gear) => SaveEquipment(new(character, _equipped.Items
        .Where(item => !Conflicts(item.Slot, item.ItemId, gear.Slot, gear.ItemId)).Append(gear).ToArray()));

    static bool Conflicts(string existing, int existingId, string replacement, int replacementId) =>
        existing == replacement || (existing == "Overall" && replacement is "Top" or "Bottom")
        || (replacement == "Overall" && existing is "Top" or "Bottom")
        || (existing == "Shield" && replacement == "Weapon" && IsTwoHanded(replacementId))
        || (replacement == "Shield" && existing == "Weapon" && IsTwoHanded(existingId));

    static bool IsTwoHanded(int id) => id / 10000 is >= 140 and <= 146;

    static string SlotOf(int id, string chosen)
    {
        string slot = DefaultSlot(id);
        return slot == "Ring 1" && chosen.StartsWith("Ring ", StringComparison.Ordinal) || slot == "Other" && chosen.Length > 0 ? chosen : slot;
    }

    static string DefaultSlot(int id) => (id / 10000) switch
    {
        100 => "Hat", 101 => "Face", 102 => "Eye", 103 => "Earrings", 104 => "Top", 105 => "Overall", 106 => "Bottom",
        107 => "Shoes", 108 => "Gloves", 109 => "Shield", 110 => "Cape", 111 => "Ring 1", 112 => "Pendant",
        113 => "Belt", 114 => "Medal", 115 => "Shoulder", >= 130 and <= 149 => "Weapon",
        206 or 207 or 233 => "Projectile", _ => "Other",
    };

    void ResetPreview()
    {
        _data.Preview.Clear();
        _previewPortrait.Source = null;
        _importedPortrait?.Dispose();
        _importedPortrait = null;
        Persist();
        RefreshLoadouts();
    }

    void RefreshLoadouts()
    {
        _currentRows.Children.Clear();
        _currentIcons.Children.Clear();
        _previewRows.Children.Clear();
        _previewIcons.Children.Clear();
        foreach (var item in _equipped.Items.OrderBy(i => Array.IndexOf(Slots, i.Slot)))
        {
            var remove = Button("−", () =>
            {
                try { SaveEquipment(_equipped with { Items = _equipped.Items.Where(i => i.Slot != item.Slot).ToArray() }); }
                catch (IOException ex) { Status(ex.Message); }
            });
            _currentRows.Children.Add(GearRow(item.Slot, item.ItemId, item.Stats,
                Button("Edit", () => EditGear(item.Slot, item.ItemId, item.Stats, item.RemainingEnhancements, true)), remove));
            _currentIcons.Children.Add(Icon(item.ItemId));
        }
        if (_equipped.Items.Count == 0) _currentRows.Children.Add(Muted("No worn gear recorded yet."));
        foreach (var item in _data.Preview)
        {
            var remove = Button("−", () => { _data.Preview.Remove(item); Persist(); RefreshLoadouts(); });
            _previewRows.Children.Add(GearRow(item.Slot, item.ItemId, item.Stats,
                Button("Edit", () => EditGear(item.Slot, item.ItemId, item.Stats, item.RemainingEnhancements, item.Owned)), remove));
        }

        foreach (var item in PreviewLoadout().OrderBy(i => Array.IndexOf(Slots, i.Slot))) _previewIcons.Children.Add(Icon(item.ItemId));
        if (_data.Portrait is { Length: AvatarReader.W * AvatarReader.H } pixels
            && (_equipped.Character.Length == 0 || _data.PortraitCharacter.Equals(_equipped.Character, StringComparison.OrdinalIgnoreCase)))
        {
            var frame = new Frame(AvatarReader.W, AvatarReader.H);
            pixels.CopyTo(frame.Px, 0);
            _portrait.Source = ToBitmap(frame, _portrait.Source as WriteableBitmap);
        }
        else
        {
            (_portrait.Source as IDisposable)?.Dispose();
            _portrait.Source = null;
        }
        _portrait.IsVisible = _portrait.Source != null;
        _previewPortrait.IsVisible = _previewPortrait.Source != null;
        UpdateResults();
    }

    Control GearRow(string slot, int item, string stats, Button edit, Button remove)
    {
        ToolTip.SetTip(remove, $"Remove {slot} gear");
        return Card(Stack(3, Wrap(Icon(item), Title($"{slot} · {_ctx.Items.Name(item)}"), edit, remove),
            Muted(stats.Length == 0 ? "Stats unavailable" : stats.Replace("\n", "  ·  "))), 7);
    }

    Control Icon(int id)
    {
        if (!_icons.TryGetValue(id, out var bitmap) && _ctx.Items.IconPath(id) is { } path)
        {
            try { _icons[id] = bitmap = new Bitmap(path); }
            catch (Exception ex) when (ex is IOException or ArgumentException) { _ctx.Log(ex.Message); }
        }
        var icon = new Image { Width = 30, Height = 30, Source = bitmap };
        RenderOptions.SetBitmapInterpolationMode(icon, BitmapInterpolationMode.None);
        ToolTip.SetTip(icon, _ctx.Items.Name(id));
        return icon;
    }

    void EditGear(string slot, int id, string stats, string enhancements, bool owned)
    {
        _gearSearch.Text = id.ToString(CultureInfo.InvariantCulture);
        _gear.SelectedIndex = 0;
        _slot.SelectedItem = slot;
        _stats.Text = stats;
        _enhancements.Text = enhancements;
        _owned.IsChecked = owned;
        _stats.Focus();
    }

    WornItem[] PreviewLoadout()
    {
        var result = _equipped.Items.ToList();
        foreach (var change in _data.Preview)
        {
            result.RemoveAll(i => Conflicts(i.Slot, i.ItemId, change.Slot, change.ItemId));
            result.Add(new(change.Slot, change.ItemId, change.Stats, change.RemainingEnhancements));
        }
        return result.ToArray();
    }

    void UpdateResults()
    {
        if (_loading) return;
        var preview = PreviewLoadout();
        if (_target.SelectedItem is Monster target)
        {
            _targetSource.IsEnabled = target.Source.Length > 0;
            _monsterInfo.Text = $"{target.Hp:N0} HP  ·  {target.PhysicalDefense} P.DEF  ·  {target.MagicDefense} M.DEF  ·  {target.Avoid} avoid"
                + (target.Weakness.Length > 0 ? $"  ·  Weak to {target.Weakness}" : "");
            var current = ShowEstimate(_equipped.Items, target, _currentDps, _currentDetails, false);
            var planned = ShowEstimate(preview, target, _previewDps, _previewDetails, true);
            _summary.Text = current > 0 ? $"DPS change  {(planned / current - 1).ToString("+0.0%;-0.0%;0.0%", CultureInfo.CurrentCulture)}" : "";
        }
        else
        {
            _targetSource.IsEnabled = false;
            _monsterInfo.Text = "Choose a monster or add a custom target.";
            _currentDps.Text = _previewDps.Text = "—";
            _currentDetails.Text = _previewDetails.Text = _summary.Text = "";
        }
        _summary.IsVisible = !string.IsNullOrWhiteSpace(_summary.Text);
        UpdateCost();
        UpdateOwnershipHint();
    }

    void UpdateOwnershipHint()
    {
        _ownershipHint.IsVisible = _gear.SelectedItem is GearChoice choice && _market?.Owns?.Invoke(choice.Item.id) == true;
        _ownershipHint.Text = _ownershipHint.IsVisible ? "Shop Card has seen this item. Mark it owned if you already have the roll you entered." : "";
    }

    double ShowEstimate(IReadOnlyList<WornItem> items, Monster target, TextBlock value, TextBlock detail, bool preview)
    {
        var gear = GearStats.Sum(items.Select(i => GearStats.TryParse(i.Stats, out var stats) ? stats : new GearStats()));
        int weapon = items.FirstOrDefault(i => i.Slot == "Weapon")?.ItemId ?? 0;
        if (items.Count == 0 || (_data.Combat.Attack != Attack.Spell && weapon == 0))
        {
            value.Text = "—";
            detail.Text = "Record or select a weapon and its stats.";
            return 0;
        }
        if (_data.Combat.Attack == Attack.LuckySeven && weapon / 10000 != 147)
        {
            value.Text = "—";
            detail.Text = "Lucky Seven requires a claw.";
            return 0;
        }
        var damage = Combat.Estimate(_data.Combat, gear, weapon, target, preview);
        if (damage.Weapon == "Unsupported weapon")
        {
            value.Text = "—";
            detail.Text = "This weapon family has no damage model yet.";
            return 0;
        }
        value.Text = $"{damage.Dps:N1} DPS";
        string time = double.IsFinite(damage.SecondsToKill) ? $"{damage.SecondsToKill:N1}s to defeat" : "No damage";
        var statsLine = string.Join(" · ", GearStats.Names.Select((name, index) => (name, value: gear[index]))
            .Where(entry => entry.value != 0).Select(entry => $"{entry.name} {entry.value:+#;-#}"));
        var c = _data.Combat;
        detail.Text = $"{damage.HitChance:P0} hit  ·  {damage.DamagePerCast:N1} per cast  ·  {time}\n" +
            $"STR {c.Str + gear[0]} · DEX {c.Dex + gear[1]} · INT {c.Int + gear[2]} · LUK {c.Luk + gear[3]}" +
            (statsLine.Length > 0 ? $"\nGear bonuses  {statsLine}" : "");
        return damage.Dps;
    }

    void UpdateCost()
    {
        _refreshPrices.IsVisible = _data.Preview.Count > 0;
        if (_data.Preview.Count == 0) { _cost.Text = ""; return; }
        decimal known = 0;
        int missing = 0;
        var quotes = new List<string>();
        foreach (var item in _data.Preview)
        {
            string name = _ctx.Items.Name(item.ItemId);
            bool unchanged = _equipped.Items.Any(i => i.Slot == item.Slot && i.ItemId == item.ItemId
                && EquipmentReader.Profile(i.Stats, i.RemainingEnhancements) == EquipmentReader.Profile(item.Stats, item.RemainingEnhancements));
            if (item.Owned || unchanged) { quotes.Add($"{name} · owned"); continue; }
            var quote = _market?.Find?.Invoke(item.ItemId, item.Stats, item.RemainingEnhancements);
            if (quote == null && _market?.Find == null)
            {
                if (!_archivedQuotes.TryGetValue(item, out var pending))
                    _archivedQuotes[item] = pending = LoadArchiveQuoteAsync(item);
                if (pending.IsCompletedSuccessfully) quote = pending.Result;
                else if (!pending.IsCompleted)
                {
                    missing++;
                    quotes.Add($"{name} · looking up archived prices");
                    continue;
                }
            }
            if (quote != null)
            {
                known += quote.Mesos;
                quotes.Add($"{name} · {quote.Mesos:N0} mesos · {quote.Source}");
            }
            else { missing++; quotes.Add($"{name} · no saved market price"); }
        }
        _cost.Text = (missing > 0 ? $"Known cost {known:N0} mesos · {missing} unpriced" : $"Estimated cost {known:N0} mesos")
            + "\n" + string.Join("\n", quotes);
    }

    async Task<MarketQuote?> LoadArchiveQuoteAsync(PlannedGear gear)
    {
        try { return await Task.Run(() => ArchivedPrices.Find(new MarketDatabase(MarketDatabase.DefaultPath), gear), _token); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { return null; }
        catch (Exception ex) { _ctx.Log("Archive quote: " + ex.Message); return null; }
        finally { _ctx.OnUi(UpdateCost); }
    }

    void SearchMonsters(string key)
    {
        bool previous = _loading;
        _loading = true;
        var cached = _monsters.Cached.Count > 0 ? _monsters.Cached : ClassicWiki.StarterMonsters.AsEnumerable();
        var all = cached.Concat(_monsters.Curated).GroupBy(i => i.Key).Select(g => g.Last());
        var matches = all.Where(m => m.Name.Contains((_monsterSearch.Text ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Level).ThenBy(m => m.Name).ToArray();
        _target.ItemsSource = matches;
        _target.SelectedItem = matches.FirstOrDefault(m => m.Key == key) ?? matches.FirstOrDefault();
        _loading = previous;
        UpdateResults();
    }

    async Task RefreshMonstersAsync()
    {
        var feed = await ClassicWiki.RefreshMonstersAsync(_token);
        _monsters.Cached = feed.ToList();
        _monsters.RefreshedAt = DateTimeOffset.UtcNow;
        _ctx.Storage.Save("monsters", _monsters);
        SearchMonsters((_target.SelectedItem as Monster)?.Key ?? _data.SelectedMonster);
        Status($"Loaded {feed.Length} monsters from MapleClassic Wiki.");
    }

    Control BuildMonsterEditor()
    {
        var name = new TextBox { Width = 175, Watermark = "Monster name" };
        var level = Number(1, 1, 999);
        var hp = Number(1, 1, long.MaxValue);
        var pdef = Number(0, 0, 1000000);
        var mdef = Number(0, 0, 1000000);
        var avoid = Number(0, 0, 1000000);
        var weak = new TextBox { Width = 150, Watermark = "Weakness (optional)" };
        var edit = Button("Copy selected stats", () =>
        {
            if (_target.SelectedItem is not Monster target) return;
            _editingTarget = target.Key;
            name.Text = target.Name;
            level.Value = target.Level;
            hp.Value = target.Hp;
            pdef.Value = target.PhysicalDefense;
            mdef.Value = target.MagicDefense;
            avoid.Value = target.Avoid;
            weak.Text = target.Weakness;
        });
        var save = Button("Save target", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { Status("Enter the target's name."); return; }
            string key = _editingTarget.Length > 0 ? _editingTarget : "custom:" + Guid.NewGuid().ToString("N");
            var current = _target.SelectedItem as Monster;
            _monsters.Curated.RemoveAll(m => m.Key == key);
            _monsters.Curated.Add(new(key, name.Text.Trim(), (int)(level.Value ?? 1), (long)(hp.Value ?? 1),
                (int)(pdef.Value ?? 0), (int)(mdef.Value ?? 0), (int)(avoid.Value ?? 0), weak.Text ?? "", current?.Key == key ? current.Source : ""));
            SaveTargets(key);
            _editingTarget = key;
        }, true);
        var reset = Button("New target", () =>
        {
            _editingTarget = "";
            name.Text = weak.Text = "";
            level.Value = hp.Value = 1;
            pdef.Value = mdef.Value = avoid.Value = 0;
        });
        var delete = Button("Remove local entry", () =>
        {
            if (_target.SelectedItem is Monster selected && _monsters.Curated.RemoveAll(m => m.Key == selected.Key) > 0)
            {
                _editingTarget = "";
                SaveTargets(selected.Key);
            }
        });
        return new Expander { Header = "Add or curate targets", Content = Stack(5, Wrap(name, weak),
            Wrap(Stack(2, Muted("Level"), level), Stack(2, Muted("HP"), hp), Stack(2, Muted("P.DEF"), pdef),
                Stack(2, Muted("M.DEF"), mdef), Stack(2, Muted("Avoid"), avoid)), Wrap(edit, save, reset, delete)) };
    }

    void SaveTargets(string key)
    {
        try { _ctx.Storage.Save("monsters", _monsters); }
        catch (IOException ex) { Status(ex.Message); return; }
        _monsterSearch.Text = "";
        _data.SelectedMonster = key;
        SearchMonsters(key);
        Persist();
    }

    static NumericUpDown Number(decimal value, decimal min, decimal max, decimal increment = 1) =>
        new() { Value = value, Minimum = min, Maximum = max, Increment = increment, Width = max > 1_000_000 ? 140 : 105 };

    Control BuildCombatInputs()
    {
        var panel = new WrapPanel();
        void Add(string label, Control input) => panel.Children.Add(new Border { Child = Stack(2, Muted(label), input), Margin = new Thickness(0, 0, 9, 8) });
        void Field(string label, int value, int min, int max, Func<CombatSettings, int, CombatSettings> set)
        {
            var field = Number(value, min, max);
            field.ValueChanged += (_, _) =>
            {
                if (field.Value is not { } v) return;
                _data.Combat = set(_data.Combat, (int)v);
                UpdateResults();
            };
            field.LostFocus += (_, _) => Persist();
            Add(label, field);
        }
        void Pick<T>(string label, T value, Func<CombatSettings, T, CombatSettings> set) where T : struct, Enum
        {
            var field = new ComboBox { ItemsSource = Enum.GetValues<T>(), SelectedItem = value };
            field.SelectionChanged += (_, _) =>
            {
                if (field.SelectedItem is not T v) return;
                _data.Combat = set(_data.Combat, v); Persist(); UpdateResults();
            };
            Add(label, field);
        }
        var c = _data.Combat;
        Pick("Job", c.Job, (p, v) => p with { Job = v });
        Field("Level", c.Level, 1, 250, (p, v) => p with { Level = v });
        Field("Base STR", c.Str, 0, 2000, (p, v) => p with { Str = v });
        Field("Base DEX", c.Dex, 0, 2000, (p, v) => p with { Dex = v });
        Field("Base INT", c.Int, 0, 2000, (p, v) => p with { Int = v });
        Field("Base LUK", c.Luk, 0, 2000, (p, v) => p with { Luk = v });
        Pick("Attack", c.Attack, (p, v) => p with { Attack = v });
        Field("Mastery Lv", c.Mastery, 0, 10, (p, v) => p with { Mastery = v });
        Field("Skill %", c.SkillPercent, 1, 5000, (p, v) => p with { SkillPercent = v });
        Field("Hits per cast", c.Hits, 1, 30, (p, v) => p with { Hits = v });
        Field("Current cycle (ms)", c.CycleMs, 10, 60000, (p, v) => p with { CycleMs = v });
        Field("Preview cycle (ms)", c.PreviewCycleMs, 0, 60000, (p, v) => p with { PreviewCycleMs = v });
        var element = Number((decimal)c.ElementMultiplier, 0, 3, 0.05m);
        element.ValueChanged += (_, _) =>
        {
            if (element.Value is not { } v) return;
            _data.Combat = _data.Combat with { ElementMultiplier = (double)v };
            UpdateResults();
        };
        element.LostFocus += (_, _) => Persist();
        Add("Element ×", element);
        return new Expander { Header = "Combat inputs", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Stack(6, panel,
            Muted("Use base stats before gear. Preview cycle 0 uses the current cycle. Element × 1 is neutral. DPS assumes continuous attacks on one target."),
            Button("Combat formulas · NiaDB ↗", () => OpenUrl(ClassicWiki.FormulaUrl))) };
    }
}

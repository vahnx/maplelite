using MapleLite.Core.Capture;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>
/// What MapleLite knows about your game right now, read once for every plugin (<c>context.Game</c>): where you are,
/// the channel, your name, and HP / MP / EXP. Plugins should take these from here rather than reading the minimap,
/// status bar or channel window themselves, so every plugin sees the same values.
/// Everything is null until it has been read, and keeps its last value while it can't be seen.
/// </summary>
public interface IGameState : IMapSource
{
    /// <summary>The map name as the minimap shows it, with the room number when there is one ("Free Market &lt;1&gt;").</summary>
    string? Location { get; }
    /// <summary>Which size the minimap is showing at, or null before the first picture.</summary>
    MinimapState? Minimap { get; }
    /// <summary>The channel you're on. Only seen while the game's Change Channel window is open; kept after it closes.</summary>
    int? Channel { get; }
    /// <summary>Your character's name, from the status bar.</summary>
    string? Ign { get; }
    /// <summary>The status bar's numbers (HP, MP, EXP), or null until it has been read.</summary>
    StatusBarReading? Status { get; }
    /// <summary>When <see cref="Status"/> was last read.</summary>
    DateTimeOffset? StatusAt { get; }

    int? Hp => Status?.Hp;
    int? HpMax => Status?.HpMax;
    int? Mp => Status?.Mp;
    int? MpMax => Status?.MpMax;
    long? Exp => Status?.Exp;
    double? ExpPercent => Status?.ExpPercent;

    /// <summary>Something above changed. Raised on a background thread: use context.OnUi for UI work.</summary>
    event Action? Changed;
}

/// <summary>
/// Reads the game state shared with plugins (<see cref="IGameState"/>) from the host's capture loop, twice a second.
/// The status bar and channel window are read from pixels every time; the map name (OCR) every two seconds and the
/// name (OCR) every six once known. Names change only after reading the same twice in a row, so one bad read is ignored.
/// </summary>
public sealed class GameWatcher : IGameState, IAsyncDisposable
{
    const double Fps = 2;

    readonly IFrameSource _frames;
    readonly IOcr _ocr;
    readonly CancellationTokenSource _stop = new();
    readonly MapWatcher _map = new();
    readonly StatusBarReader _bar = new();
    IDisposable? _sub;
    Task? _loop;
    GameFrame? _latest;
    string? _ignCandidate;
    long _tick;

    public GameWatcher(IFrameSource frames, IOcr ocr) { _frames = frames; _ocr = ocr; }

    public string? Map => _map.Map;
    public string? Problem { get; private set; }
    public string? Location { get; private set; }
    public MinimapState? Minimap { get; private set; }
    public int? Channel { get; private set; }
    public string? Ign { get; private set; }
    public StatusBarReading? Status { get; private set; }
    public DateTimeOffset? StatusAt { get; private set; }
    public event Action? Changed;

    public void Start()
    {
        _loop = Task.Run(Loop);
        _sub = _frames.Subscribe(gf => Interlocked.Exchange(ref _latest, gf), Fps);
    }

    public async Task StopAsync()
    {
        _sub?.Dispose();
        _stop.Cancel();
        if (_loop != null) await _loop.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Takes one read of the name; returns the name to use from now on (the first read is taken as is).</summary>
    public string? TakeIgn(string? read)
    {
        if (string.IsNullOrWhiteSpace(read)) return Ign;
        if (Ign == null || read == Ign) { Ign = read; _ignCandidate = null; return Ign; }
        if (read == _ignCandidate) { Ign = read; _ignCandidate = null; }
        else _ignCandidate = read;
        return Ign;
    }

    async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var gf = Interlocked.Exchange(ref _latest, null);
            if (gf != null)
            {
                bool changed = false;
                try { changed = await ReadAsync(gf).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception e) { Problem = "game state: " + e.Message; }
                if (changed)
                    try { Changed?.Invoke(); } catch { /* a plugin's handler; not ours to fail on */ }
            }
            try { await Task.Delay(gf == null ? 100 : 250, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    async Task<bool> ReadAsync(GameFrame gf)
    {
        var f = gf.Frame;
        bool changed = false;
        long tick = _tick++;

        if (_bar.Read(f) is { } bar)
        {
            if (Status is not { } old || old.Hp != bar.Hp || old.HpMax != bar.HpMax || old.Mp != bar.Mp || old.MpMax != bar.MpMax || old.Exp != bar.Exp) changed = true;
            Status = bar; StatusAt = gf.Time;
        }

        if (ChannelReader.Read(f) is int ch && ch != Channel) { Channel = ch; changed = true; }

        var state = MapReader.State(f);
        if (state != Minimap) { Minimap = state; changed = true; }
        if (tick % 4 == 0)
        {
            var (_, text) = await MapReader.ReadAsync(_ocr, f).ConfigureAwait(false);
            var before = (Map, Location);
            _map.Take(text);
            // the shown name (with its room) is kept once the map it names has settled
            if (Map != null && string.Equals(MapWatcher.Clean(text), Map, StringComparison.OrdinalIgnoreCase)) Location = text!.Trim();
            else if (Map != before.Map) Location = Map;
            if ((Map, Location) != before) changed = true;
            Problem = state switch
            {
                MinimapState.NameHidden => "the minimap is at its middle size, which hides the map name",
                MinimapState.NotFound => "the minimap isn't showing",
                _ => null,
            };
        }

        if (tick % (Ign == null ? 4 : 12) == 2)
        {
            var before = Ign;
            TakeIgn(await IgnReader.ReadAsync(_ocr, f).ConfigureAwait(false));
            if (Ign != before) changed = true;
        }
        return changed;
    }
}

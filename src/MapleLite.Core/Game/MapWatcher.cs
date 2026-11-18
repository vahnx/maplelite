using System.Text.RegularExpressions;
using MapleLite.Core.Capture;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>The map you are on, read from the minimap by MapleLite (one reader shared by every plugin).</summary>
public interface IMapSource
{
    /// <summary>The map name without its room number ("Free Market"), or null until it has been read.</summary>
    string? Map { get; }
    /// <summary>Why the map can't be read right now (minimap at its middle size, or not showing), or null when it can.</summary>
    string? Problem { get; }
}

/// <summary>
/// Keeps the name of the map you're on, read from the minimap every couple of seconds. MapleLite runs one and shares it
/// with plugins as <c>context.Map</c> (Loot files pickups by it, XP Tracker files EXP by it). A new name is taken only once it reads the same twice in a row (one odd read is OCR),
/// and the last name is kept while the minimap can't be read (collapsed to its middle size, or covered).
/// </summary>
public sealed partial class MapWatcher : IMapSource, IAsyncDisposable
{
    const double Fps = 0.5;

    readonly IFrameSource _frames;
    readonly IOcr _ocr;
    readonly CancellationTokenSource _stop = new();
    IDisposable? _sub;
    Task? _loop;
    GameFrame? _latest;
    string? _candidate;

    public MapWatcher(IFrameSource frames, IOcr ocr) { _frames = frames; _ocr = ocr; }

    /// <summary>The map you're on, or null until it's been read.</summary>
    public string? Map { get; private set; }

    /// <summary>Why the map can't be read right now, or null when it can.</summary>
    public string? Problem { get; private set; }

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

    /// <summary>The map name without its room number ("Free Market &lt;1&gt;" → "Free Market"), so every room files together.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var name = RoomSuffix().Replace(text, "").Trim(' ', '-', ':');
        return name.Count(char.IsLetter) >= 3 ? name : null;
    }

    /// <summary>Takes one read; returns the map name to use from now on.</summary>
    public string? Take(string? read)
    {
        var name = Clean(read);
        if (name == null) return Map;
        if (string.Equals(name, Map, StringComparison.OrdinalIgnoreCase)) { _candidate = null; return Map; }
        if (string.Equals(name, _candidate, StringComparison.OrdinalIgnoreCase)) { Map = name; _candidate = null; }
        else _candidate = name;
        return Map;
    }

    async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var gf = Interlocked.Exchange(ref _latest, null);
            if (gf != null)
                try
                {
                    var (state, text) = await MapReader.ReadAsync(_ocr, gf.Frame).ConfigureAwait(false);
                    Take(text);
                    Problem = state switch
                    {
                        MinimapState.NameHidden => "the minimap is at its middle size, which hides the map name",
                        MinimapState.NotFound => "the minimap isn't showing",
                        _ => null,
                    };
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception e) { Problem = "map name: " + e.Message; }
            try { await Task.Delay(gf == null ? 100 : 500, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    [GeneratedRegex(@"\s*[<(\[]\s*\d{1,3}\s*[>)\]]\s*$")]
    private static partial Regex RoomSuffix();
}

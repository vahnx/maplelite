using MapleLite.Core.Capture;
using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Plugins.Loot;

/// <summary>
/// Follows the meso total in the inventory window while it's open, and reports meso pickups the notices didn't show
/// (see <see cref="MesoReconciler"/>). Looks for the window every few seconds while it isn't found.
/// </summary>
internal sealed class MesoWatcher
{
    const double Fps = 4;
    static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(5); // a whole-picture read takes most of a second
    const int MissesToLose = 3;

    readonly IFrameSource _frames;
    readonly IOcr _ocr;
    readonly Action<DateTimeOffset, long, string> _onMissed;
    readonly Action<string> _note;
    readonly CancellationTokenSource _stop = new();
    public MesoReconciler Reconciler { get; } = new();
    IDisposable? _sub;
    Task? _loop;
    GameFrame? _latest;

    public MesoWatcher(IFrameSource frames, IOcr ocr, Action<DateTimeOffset, long, string> onMissed, Action<string> note)
    {
        _frames = frames; _ocr = ocr; _onMissed = onMissed; _note = note;
    }

    /// <summary>Where the total was found, or null while the inventory isn't open.</summary>
    public Region? Box { get; private set; }
    public string? Error { get; private set; }

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

    async Task Loop()
    {
        var lastLook = DateTimeOffset.MinValue;
        int misses = 0;
        long? lastSettledShown = null;
        while (!_stop.IsCancellationRequested)
        {
            var gf = Interlocked.Exchange(ref _latest, null);
            if (gf != null)
                try
                {
                    var f = gf.Frame;
                    if (Box is { } box && box.Right <= f.W && box.Bottom <= f.H)
                    {
                        if (await MesoTotal.ReadAsync(_ocr, f, box, _stop.Token).ConfigureAwait(false) is { } total)
                        {
                            misses = 0;
                            long missed = Reconciler.Read(total, gf.Time);
                            if (missed > 0)
                            {
                                _note($"meso total settled at {total:N0}: {missed:N0} more than the notices showed => counted +{missed:N0} mesos");
                                _onMissed(gf.Time, missed, $"Meso total rose to {total:N0}; {missed:N0} picked up without a notice showing");
                            }
                            if (total != lastSettledShown) { _note($"meso total {total:N0}"); lastSettledShown = total; }
                        }
                        else if (++misses >= MissesToLose) { Box = null; Reconciler.Lost(); _note("meso total lost (inventory closed or moved)"); }
                    }
                    else if (gf.Time - lastLook >= LookEvery)
                    {
                        lastLook = gf.Time;
                        if (await MesoTotal.LocateAsync(_ocr, f, _stop.Token).ConfigureAwait(false) is { } found)
                        {
                            Box = found.Box; misses = 0; lastSettledShown = found.Value;
                            Reconciler.Read(found.Value, gf.Time);
                            _note($"meso total found at {found.Box}: {found.Value:N0}");
                        }
                    }
                    Error = null;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception e) { Error = "Meso total: " + e.Message; }
            try { await Task.Delay(gf == null ? 30 : 200, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
}

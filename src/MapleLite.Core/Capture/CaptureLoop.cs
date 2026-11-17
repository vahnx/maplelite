using System.Diagnostics;
using MapleLite.Core.Imaging;
using MapleLite.Core.Platform;

namespace MapleLite.Core.Capture;

public enum CaptureState { Stopped, Searching, Capturing, Minimized, Blocked, Error }

/// <summary>One picture of the game's client area, shared by every subscriber.</summary>
public sealed record GameFrame(Frame Frame, GameWindow Window, DateTimeOffset Time);

public sealed record CaptureStatus(CaptureState State, string Message, GameWindow? Window, double Fps, long Frames)
{
    public static readonly CaptureStatus Stopped = new(CaptureState.Stopped, "Capture is stopped.", null, 0, 0);
}

/// <summary>Game frames for plugins. They never capture on their own.</summary>
public interface IFrameSource
{
    CaptureStatus Status { get; }
    GameFrame? Latest { get; }
    event Action<CaptureStatus>? StatusChanged;

    /// <summary>
    /// Calls <paramref name="onFrame"/> with new frames, at most <paramref name="fps"/> times a second.
    /// The call runs on the capture thread: keep it short and hand slow work (OCR) to a task or queue.
    /// The loop captures at the fastest rate any subscriber asks for (capped at 30).
    /// Dispose the result to stop.
    /// </summary>
    IDisposable Subscribe(Action<GameFrame> onFrame, double fps);
}

/// <summary>
/// The host's single capture loop: finds the game window, follows it, captures its client area,
/// and hands each frame to subscribers. Pure logic over <see cref="IPlatform"/>, so tests can drive it.
/// </summary>
public sealed class CaptureLoop : IFrameSource, IAsyncDisposable
{
    public const double MaxFps = 30;
    const double IdleFps = 2;

    readonly IPlatform _platform;
    readonly object _lock = new();
    readonly List<Subscription> _subs = new();
    CancellationTokenSource? _stop;
    Task? _task;
    volatile CaptureStatus _status = CaptureStatus.Stopped;
    volatile GameFrame? _latest;

    public CaptureLoop(IPlatform platform) => _platform = platform;

    public GameTarget Target { get; set; } = new();
    public CaptureStatus Status => _status;
    public GameFrame? Latest => _latest;
    public event Action<CaptureStatus>? StatusChanged;
    public bool Running => _task is { IsCompleted: false };

    sealed class Subscription(CaptureLoop owner, Action<GameFrame> handler, double fps) : IDisposable
    {
        public readonly Action<GameFrame> Handler = handler;
        public readonly double Fps = Math.Clamp(fps, 0.1, MaxFps);
        public long LastTicks;
        public void Dispose() { lock (owner._lock) owner._subs.Remove(this); }
    }

    public IDisposable Subscribe(Action<GameFrame> onFrame, double fps)
    {
        var s = new Subscription(this, onFrame, fps);
        lock (_lock) _subs.Add(s);
        return s;
    }

    double WantedFps()
    {
        lock (_lock) return _subs.Count == 0 ? IdleFps : Math.Min(MaxFps, _subs.Max(s => s.Fps));
    }

    public void Start()
    {
        if (Running) return;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _task = Task.Run(() => RunAsync(token));
    }

    public async Task StopAsync()
    {
        _stop?.Cancel();
        if (_task != null) { try { await _task.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        _task = null;
        SetStatus(CaptureStatus.Stopped);
    }

    void SetStatus(CaptureStatus s)
    {
        _status = s;
        try { StatusChanged?.Invoke(s); } catch { /* a bad listener must not stop capture */ }
    }

    async Task RunAsync(CancellationToken ct)
    {
        GameWindow? window = null;
        var lastLookup = Stopwatch.StartNew();
        var fpsClock = Stopwatch.StartNew();
        long frames = 0, framesAtMark = 0; double fps = 0;
        int blackRun = 0;

        while (!ct.IsCancellationRequested)
        {
            var tick = Stopwatch.StartNew();
            double delayMs = 1000;
            try
            {
                if (window == null || lastLookup.ElapsedMilliseconds > 1000)
                {
                    window = window == null ? _platform.Finder.Find(Target) : _platform.Finder.Refresh(window) ?? _platform.Finder.Find(Target);
                    lastLookup.Restart();
                }
                if (window == null)
                {
                    SetStatus(new(CaptureState.Searching, $"Looking for the game ({Describe(Target)}). Start MapleStory or pick its window.", null, 0, frames));
                }
                else if (window.Mode == WindowMode.Minimized)
                {
                    SetStatus(new(CaptureState.Minimized, "The game is minimized. Restore it; it can stay behind other windows.", window, 0, frames));
                }
                else
                {
                    var frame = await _platform.Capture.CaptureClientAsync(window, ct).ConfigureAwait(false);
                    blackRun = ImageOps.LooksBlack(frame) ? blackRun + 1 : 0;
                    if (blackRun >= 5)
                    {
                        SetStatus(new(CaptureState.Blocked, window.Mode == WindowMode.ExclusiveFullscreen
                            ? "The game is in exclusive fullscreen and its picture comes through black. Switch the game to windowed or borderless."
                            : "The game's picture comes through black. The game may be loading, or capture is being blocked.", window, fps, frames));
                    }
                    else
                    {
                        var gf = new GameFrame(frame, window, DateTimeOffset.Now);
                        _latest = gf;
                        frames++;
                        Publish(gf);
                        if (fpsClock.ElapsedMilliseconds >= 1000)
                        {
                            fps = (frames - framesAtMark) * 1000.0 / fpsClock.ElapsedMilliseconds;
                            framesAtMark = frames; fpsClock.Restart();
                        }
                        SetStatus(new(CaptureState.Capturing, $"Capturing {frame.W}x{frame.H} ({ModeText(window.Mode)}).", window, fps, frames));
                    }
                    delayMs = 1000.0 / WantedFps();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                window = null; // look the window up again next time
                SetStatus(new(CaptureState.Error, "Capture failed: " + e.Message, null, 0, frames));
            }

            int wait = (int)Math.Max(1, delayMs - tick.ElapsedMilliseconds);
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    void Publish(GameFrame gf)
    {
        Subscription[] subs;
        lock (_lock) subs = _subs.ToArray();
        long now = Stopwatch.GetTimestamp();
        foreach (var s in subs)
        {
            if (now - s.LastTicks < Stopwatch.Frequency / s.Fps * 0.9) continue;
            s.LastTicks = now;
            try { s.Handler(gf); } catch { /* one failing plugin must not stop the others */ }
        }
    }

    static string Describe(GameTarget t) => t.ProcessId is { } pid ? $"process {pid}" : $"\"{t.ProcessName}\"";

    public static string ModeText(WindowMode m) => m switch
    {
        WindowMode.Windowed => "windowed",
        WindowMode.Borderless => "borderless fullscreen",
        WindowMode.ExclusiveFullscreen => "exclusive fullscreen",
        WindowMode.Minimized => "minimized",
        _ => "unknown mode",
    };

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

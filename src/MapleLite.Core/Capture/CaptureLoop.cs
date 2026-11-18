using System.Diagnostics;
using MapleLite.Core.Imaging;
using MapleLite.Core.Platform;

namespace MapleLite.Core.Capture;

public enum CaptureState { Stopped, Searching, Capturing, Minimized, Blocked, Error }

/// <summary>
/// One picture of the game, shared by every subscriber. Black letterbox bars are already cut off, so the game's
/// top-left is at (0, 0); <paramref name="Offset"/> is where that sits in the window's client area.
/// </summary>
public sealed record GameFrame(Frame Frame, GameWindow Window, DateTimeOffset Time, (int X, int Y) Offset = default);

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
    /// While anyone subscribes the loop captures at the rate the user chose (<see cref="CaptureLoop.TargetFps"/>,
    /// up to 60); each subscriber gets at most the rate it asks for.
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
    public const double MinFps = 5, MaxFps = 60, DefaultFps = 30;
    const double IdleFps = 2;
    double _targetFps = DefaultFps;

    readonly IPlatform _platform;
    readonly object _lock = new();
    readonly List<Subscription> _subs = new();
    CancellationTokenSource? _stop;
    Task? _task;
    volatile CaptureStatus _status = CaptureStatus.Stopped;
    volatile GameFrame? _latest;

    public CaptureLoop(IPlatform platform) => _platform = platform;

    public GameTarget Target { get; set; } = new();

    /// <summary>How many pictures a second to take while anyone is subscribed (5–60). The user picks it in Settings.</summary>
    public double TargetFps { get => _targetFps; set => _targetFps = Math.Clamp(value, MinFps, MaxFps); }
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
        lock (_lock) return _subs.Count == 0 ? IdleFps : _targetFps;
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
        // Frames follow a schedule rather than "wait N ms after each one": timers tick in ~15.6 ms steps on Windows,
        // so a fixed wait after every frame rounds each period up (asking for 20/s gave about 19/s, 30/s about 21/s).
        // On a schedule a late frame shortens the next wait, so the average comes out at the rate asked for.
        var clock = Stopwatch.StartNew();
        double nextMs = 0;

        while (!ct.IsCancellationRequested)
        {
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
                        // Borderless fullscreen on a monitor of another shape: the game sits between black bars.
                        // Readers measure from the game's corner, so cut the bars off here, once, for everyone.
                        var content = ImageOps.Letterbox(frame);
                        if (content != frame.Bounds) frame = frame.Crop(content);
                        var gf = new GameFrame(frame, window, DateTimeOffset.Now, (content.X, content.Y));
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

            double now = clock.Elapsed.TotalMilliseconds;
            nextMs += delayMs;
            if (nextMs < now - delayMs) nextMs = now; // more than a frame behind (slow capture): don't catch up in a burst
            int wait = (int)Math.Round(nextMs - now);
            try
            {
                if (wait > 0) await Task.Delay(wait, ct).ConfigureAwait(false);
                else await Task.Yield();
            }
            catch (OperationCanceledException) { break; }
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

using MapleLite.Core;
using MapleLite.Core.Capture;
using MapleLite.Core.Game;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;
using MapleLite.Host.Plugins;
using MapleLite.Host.Themes;
using MapleLite.Sdk;

namespace MapleLite.Host;

/// <summary>Everything the host owns: platform, the single capture loop, shared OCR, events, themes, plugins.</summary>
internal sealed class Shell : IAsyncDisposable
{
    public HostSettings Settings { get; }
    public IPlatform Platform { get; }
    public OcrService Ocr { get; }
    public CaptureLoop Capture { get; }
    /// <summary>The map you are on, read once for every plugin (<c>context.Map</c>).</summary>
    public MapWatcher Map { get; }
    public EventBus Events { get; } = new();
    public ThemeService Themes { get; }
    /// <summary>The shared item catalog (GameData/Items), loaded on first use and kept for the app's lifetime.</summary>
    public MapleLite.Core.Game.ItemCatalog Items => _items.Value;
    readonly Lazy<MapleLite.Core.Game.ItemCatalog> _items = new(MapleLite.Core.Game.ItemCatalog.LoadShipped);
    public PluginManager Plugins { get; }
    /// <summary>The click-through window over the game. Made on the UI thread once the shell exists (see App).</summary>
    public OverlayService Overlay { get; set; } = null!;
    /// <summary>Records the game picture for replaying later (Game page).</summary>
    public SessionRecorder Recorder { get; }
    public event Action<string>? Notified;
    readonly string _logPath = Path.Combine(AppPaths.Root, "maplelite.log");

    /// <summary>Doesn't touch the UI, so it can be built off the UI thread while the startup window shows progress.</summary>
    public Shell(HostSettings settings, ThemeService themes)
    {
        Settings = settings;
        Themes = themes;
        var real = Program.CreatePlatform();
        Platform = Options.ReplayPath != null ? new ReplayPlatform(Options.ReplayPath, real, Options.ReplayLoop) : real;
        Ocr = new OcrService(Platform.Ocr);
        Capture = new CaptureLoop(Platform) { Target = new GameTarget(Settings.ProcessName, Settings.ProcessId), TargetFps = Settings.CaptureFps };
        Map = new MapWatcher(Capture, Ocr);
        Map.Start();
        Events.OnHandlerError = e => Log("event handler failed: " + e);
        Plugins = new PluginManager(this);
        Recorder = new SessionRecorder(this);
    }

    public void SetTarget(GameTarget target)
    {
        Capture.Target = target;
        Settings.ProcessName = target.ProcessName;
        Settings.ProcessId = target.ProcessId;
        Settings.Save();
    }

    public void Notify(string message) { Log(message); Notified?.Invoke(message); }

    public void Log(string message)
    {
        try { File.AppendAllText(_logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); } catch (IOException) { }
    }

    /// <summary>
    /// Closes everything in order, reporting each step: the recording, every plugin (each given time to save),
    /// then capture and the OS services.
    /// </summary>
    public async Task CloseAsync(Func<double, string, Task>? report = null, Action<string, bool>? note = null)
    {
        report ??= (_, _) => Task.CompletedTask;
        if (Recorder.Active) { await report(2, "Saving the recording…"); await Recorder.StopAsync(); note?.Invoke("Recording saved", false); }
        await Plugins.StopAllAsync(
            (p, i, n) => report(5 + 85.0 * i / n, $"Saving {p.Manifest.Name}…"),
            (p, took, problem) =>
            {
                Log($"[{p.Manifest.Id}] stopped in {took.TotalMilliseconds:0} ms{(problem != null ? ": " + problem : "")}");
                note?.Invoke(problem == null ? $"✓ {p.Manifest.Name} ({took.TotalSeconds:0.0} s)" : $"✗ {p.Manifest.Name} {problem}", problem != null);
            },
            TimeSpan.FromSeconds(10));
        Overlay?.Dispose();
        await report(92, "Stopping capture…");
        await Map.DisposeAsync();
        await Capture.DisposeAsync();
        Ocr.Dispose();
        Platform.Dispose();
        await report(100, "Done");
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}

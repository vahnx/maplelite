using MapleLite.Core;
using MapleLite.Core.Capture;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;
using MapleLite.Host.Plugins;
using MapleLite.Host.Themes;

namespace MapleLite.Host;

/// <summary>Everything the host owns: platform, the single capture loop, shared OCR, themes, plugins.</summary>
internal sealed class Shell : IAsyncDisposable
{
    public HostSettings Settings { get; }
    public IPlatform Platform { get; }
    public OcrService Ocr { get; }
    public CaptureLoop Capture { get; }
    public ThemeService Themes { get; } = new();
    public PluginManager Plugins { get; } = new();
    public event Action<string>? Notified;
    readonly string _logPath = Path.Combine(AppPaths.Root, "maplelite.log");

    public Shell()
    {
        Settings = HostSettings.Load();
        var real = Program.CreatePlatform();
        Platform = Options.ReplayPath != null ? new ReplayPlatform(Options.ReplayPath, real) : real;
        Ocr = new OcrService(Platform.Ocr);
        Capture = new CaptureLoop(Platform) { Target = new GameTarget(Settings.ProcessName, Settings.ProcessId) };
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

    public async ValueTask DisposeAsync()
    {
        await Plugins.StopAllAsync();
        await Capture.DisposeAsync();
        Ocr.Dispose();
        Platform.Dispose();
    }
}

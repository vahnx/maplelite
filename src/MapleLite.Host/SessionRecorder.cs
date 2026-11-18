using MapleLite.Core;
using MapleLite.Core.Capture;
using MapleLite.Sdk;

namespace MapleLite.Host;

/// <summary>
/// Records the game picture to a folder under <c>recordings/</c> for replaying later with <c>--replay &lt;folder&gt;</c>.
/// It listens to the shared capture loop like any plugin; it never captures on its own.
/// </summary>
internal sealed class SessionRecorder(Shell shell)
{
    public const double DefaultFps = 5;
    public static readonly double[] Rates = [2, 5, 10, 15];

    Recording.Writer? _writer;
    IDisposable? _sub;

    public static string RecordingsFolder => AppPaths.Sub("recordings");
    public Recording.Writer? Current => _writer;
    public bool Active => _writer != null;
    public event Action? Changed;

    public void Start(double fps)
    {
        if (_writer != null) return;
        var started = DateTimeOffset.Now;
        var folder = Path.Combine(RecordingsFolder, started.ToString("yyyyMMdd-HHmmss"));
        var w = shell.Capture.Latest?.Window ?? shell.Capture.Status.Window;
        _writer = new Recording.Writer(folder, new Recording.Info(started, fps, w?.Title, w == null ? null : CaptureLoop.ModeText(w.Mode), w?.Scale ?? 1));
        var writer = _writer;
        _sub = shell.Capture.Subscribe(gf => writer.Add(gf.Frame, gf.Time), fps);
        shell.Log($"recording started: {folder} at {fps:0.#}/s");
        Changed?.Invoke();
    }

    public async Task<string?> StopAsync()
    {
        var writer = _writer;
        if (writer == null) return null;
        _sub?.Dispose(); _sub = null;
        _writer = null;
        await writer.DisposeAsync();
        shell.Log($"recording stopped: {writer.Folder}, {writer.Frames} frames ({writer.Saved} saved, {writer.Dropped} dropped), {writer.Bytes / 1048576.0:0} MB");
        Changed?.Invoke();
        return writer.Folder;
    }
}

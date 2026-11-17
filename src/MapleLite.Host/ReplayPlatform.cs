using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;

namespace MapleLite.Host;

/// <summary>
/// Serves a saved capture as if it were the game window, with the real OS OCR engine.
/// Lets the host and every plugin be tested without touching the live game.
/// </summary>
internal sealed class ReplayPlatform : IPlatform, IGameWindowFinder, IWindowCapture, ICursor, IWindowList
{
    readonly IPlatform _real;
    readonly Frame _frame;
    readonly GameWindow _window;

    public ReplayPlatform(string png, IPlatform real)
    {
        _real = real;
        _frame = FrameIo.LoadPng(png);
        var r = new Region(0, 0, _frame.W, _frame.H);
        _window = new GameWindow(1, 0, "replay", Path.GetFileName(png), r, r, WindowMode.Windowed, 1);
        Notes = [$"Replaying {Path.GetFileName(png)} instead of the game.", .. real.Notes];
    }

    public string Name => _real.Name + " (replay)";
    public IGameWindowFinder Finder => this;
    public IWindowCapture Capture => this;
    public ICursor Cursor => this;
    public IWindowList Windows => this;
    public IOcrEngine? Ocr => _real.Ocr;
    public IClipboardImage? Clipboard => _real.Clipboard;
    public PermissionState ScreenCapturePermission() => PermissionState.NotRequired;
    public IReadOnlyList<string> Notes { get; }

    public GameWindow? Find(GameTarget target) => _window;
    public GameWindow? Refresh(GameWindow window) => _window;
    public IReadOnlyList<WindowCandidate> ListCandidates() => [new(_window.Handle, 0, "replay", _window.Title)];
    public IReadOnlyList<WindowCandidate> TopLevelWindows() => ListCandidates();
    // The same frame object each time: an unchanged picture, so repeat OCR reads come from the cache.
    public Task<Frame> CaptureClientAsync(GameWindow window, CancellationToken ct) => Task.FromResult(_frame);
    public Task<Frame> CaptureWindowAsync(nint handle, CancellationToken ct) => Task.FromResult(_frame);
    public (int X, int Y)? PositionIn(GameWindow window) => null;

    public void Dispose() => _real.Dispose();
}

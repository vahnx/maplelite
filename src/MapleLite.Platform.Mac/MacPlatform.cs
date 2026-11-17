using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;

namespace MapleLite.Platform.Mac;

/// <summary>
/// macOS platform (milestone 5). Planned:
///  - window finding: CGWindowListCopyWindowInfo (owner name/PID, bounds, on-screen state)
///  - capture: ScreenCaptureKit (SCStream for a single window), CGWindowListCreateImage on older systems
///  - OCR: Apple Vision VNRecognizeTextRequest (accurate level, en-US)
///  - cursor: CGEventGetLocation
///  - needs Screen Recording permission (CGPreflightScreenCaptureAccess / CGRequestScreenCaptureAccess)
///  - Retina: capture is in physical pixels; GameWindow.Scale carries the backing scale factor
/// </summary>
public sealed class MacPlatform : IPlatform
{
    sealed class NotYet : IGameWindowFinder, IWindowCapture, ICursor, IWindowList
    {
        public GameWindow? Find(GameTarget target) => null;
        public GameWindow? Refresh(GameWindow window) => null;
        public IReadOnlyList<WindowCandidate> ListCandidates() => [];
        public IReadOnlyList<WindowCandidate> TopLevelWindows() => [];
        public Task<Frame> CaptureClientAsync(GameWindow window, CancellationToken ct) => throw new PlatformNotSupportedException("macOS capture arrives in milestone 5.");
        public Task<Frame> CaptureWindowAsync(nint handle, CancellationToken ct) => throw new PlatformNotSupportedException("macOS capture arrives in milestone 5.");
        public (int X, int Y)? PositionIn(GameWindow window) => null;
        public void Dispose() { }
    }

    readonly NotYet _impl = new();
    public string Name => "macOS";
    public IGameWindowFinder Finder => _impl;
    public IWindowCapture Capture => _impl;
    public ICursor Cursor => _impl;
    public IWindowList Windows => _impl;
    public IOcrEngine? Ocr => null;
    public IClipboardImage? Clipboard => null;
    public PermissionState ScreenCapturePermission() => PermissionState.Unknown;
    public IReadOnlyList<string> Notes { get; } =
    [
        "macOS support is not built yet (milestone 5): the host runs, but it can't find or capture the game.",
        "It will need Screen Recording permission in System Settings > Privacy & Security.",
    ];
    public void Dispose() { }
}

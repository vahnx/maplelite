using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;

namespace MapleLite.Platform.Linux;

/// <summary>
/// Linux platform (milestone 5). There is no native Linux game client: the game runs under Wine/Proton,
/// so its window is an X11 window (directly on X11, or through XWayland on Wayland desktops). Planned:
///  - window finding: X11 _NET_CLIENT_LIST + _NET_WM_PID + WM_CLASS (Wine sets "maplestory.exe")
///  - capture: XComposite (XCompositeNameWindowPixmap + XGetImage) so covered windows still read.
///    Pure-Wayland apps can't be captured this way; those would need the xdg-desktop-portal ScreenCast
///    picker (PipeWire). The game itself is XWayland, so X11 capture should cover it.
///  - OCR: Tesseract (libtesseract, distro package "tesseract-ocr" + English data). Linux has no OS OCR.
///  - cursor: XQueryPointer
/// </summary>
public sealed class LinuxPlatform : IPlatform
{
    sealed class NotYet : IGameWindowFinder, IWindowCapture, ICursor, IWindowList
    {
        public GameWindow? Find(GameTarget target) => null;
        public GameWindow? Refresh(GameWindow window) => null;
        public IReadOnlyList<WindowCandidate> ListCandidates() => [];
        public IReadOnlyList<WindowCandidate> TopLevelWindows() => [];
        public Task<Frame> CaptureClientAsync(GameWindow window, CancellationToken ct) => throw new PlatformNotSupportedException("Linux capture arrives in milestone 5.");
        public Task<Frame> CaptureWindowAsync(nint handle, CancellationToken ct) => throw new PlatformNotSupportedException("Linux capture arrives in milestone 5.");
        public (int X, int Y)? PositionIn(GameWindow window) => null;
        public void Dispose() { }
    }

    readonly NotYet _impl = new();
    public string Name => "Linux";
    public IGameWindowFinder Finder => _impl;
    public IWindowCapture Capture => _impl;
    public ICursor Cursor => _impl;
    public IWindowList Windows => _impl;
    public IOcrEngine? Ocr => null;
    public IClipboardImage? Clipboard => null;
    public PermissionState ScreenCapturePermission() =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } ? PermissionState.Unknown : PermissionState.NotRequired;
    public IReadOnlyList<string> Notes { get; } =
    [
        "Linux support is not built yet (milestone 5): the host runs, but it can't find or capture the game.",
        "The game runs under Wine/Proton; MapleLite will read its X11/XWayland window and use Tesseract for OCR.",
    ];
    public void Dispose() { }
}

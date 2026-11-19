using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;
using MapleLite.Platform.Mac.Interop;

namespace MapleLite.Platform.Mac;

/// <summary>
/// macOS platform, for the native macOS MapleStory client.
///  - window finding: CGWindowListCopyWindowInfo (owner name/PID, bounds, on-screen state)
///  - capture: ScreenCaptureKit (one SCStream per window), CGWindowListCreateImage before macOS 12.3
///  - OCR: Apple Vision VNRecognizeTextRequest (accurate level, en-US)
///  - cursor: CGEventGetLocation
///  - needs Screen Recording permission (CGPreflightScreenCaptureAccess / CGRequestScreenCaptureAccess)
///  - Retina: window geometry is in screen pixels and GameWindow.Scale carries the backing scale factor, but game
///    frames and the cursor are shrunk back to the game's own pixels (see GamePixels), as on Windows
/// Everything goes through P/Invoke into the system frameworks, so this project builds on any OS.
/// </summary>
public sealed class MacPlatform : IPlatform
{
    readonly MacFinder _finder = new();
    readonly MacCapture _capture = new();

    public MacPlatform()
    {
        Ocr = MacOcrEngine.TryCreate();
        var notes = new List<string>
        {
            "Capture uses ScreenCaptureKit, which reads only the game window's picture, even behind other windows.",
            "MapleLite needs Screen Recording permission (System Settings > Privacy & Security > Screen Recording). "
                + "When started from Terminal, the permission belongs to Terminal. Restart MapleLite after allowing it.",
            "The game must stay on the current Space and not minimized; fullscreen games are on their own Space, so windowed is easiest.",
        };
        if (Ocr == null) notes.Add("Text reading needs macOS 10.15 or later (Apple Vision).");
        if (_capture.FallbackReason is { } why) notes.Add("Capture is using the slower fallback: " + why);
        Notes = notes;
    }

    public string Name => "macOS";
    public IGameWindowFinder Finder => _finder;
    public IWindowCapture Capture => _capture;
    public ICursor Cursor => _finder;
    public IWindowList Windows => _finder;
    public IOcrEngine? Ocr { get; }
    public IClipboardImage? Clipboard { get; } = new MacClipboard();

    public PermissionState ScreenCapturePermission()
    {
        try { return CG.CGPreflightScreenCaptureAccess() ? PermissionState.Granted : PermissionState.Denied; }
        catch (EntryPointNotFoundException) { return PermissionState.NotRequired; } // before 10.15 there was no such permission
    }

    public IReadOnlyList<string> Notes { get; }

    public void Dispose() => _capture.Dispose();
}

using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;

namespace MapleLite.Platform.Windows;

public sealed class WindowsPlatform : IPlatform
{
    readonly WindowsFinder _finder = new();
    readonly WindowsCapture _capture = new();

    public WindowsPlatform()
    {
        Ocr = WindowsOcrEngine.TryCreate();
        var notes = new List<string>
        {
            "Capture uses Windows.Graphics.Capture, which reads only the game window's picture.",
            "Exclusive fullscreen may capture black or fall back to a monitor capture. Borderless or windowed is most reliable.",
        };
        if (Ocr == null) notes.Add("No English OCR language is installed. Add one in Settings > Time & language > Language.");
        Notes = notes;
    }

    public string Name => "Windows";
    public IGameWindowFinder Finder => _finder;
    public IWindowCapture Capture => _capture;
    public ICursor Cursor => _finder;
    public IWindowList Windows => _finder;
    public IOcrEngine? Ocr { get; }
    public IClipboardImage? Clipboard { get; } = new WindowsClipboard();
    public PermissionState ScreenCapturePermission() => PermissionState.NotRequired;
    public IReadOnlyList<string> Notes { get; }

    public void Dispose() => _capture.Dispose();
}

using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Core.Platform;

/// <summary>How the game window is shown. Capture and any future overlay behave differently in each.</summary>
public enum WindowMode
{
    Unknown,
    /// <summary>A normal window with a title bar.</summary>
    Windowed,
    /// <summary>Fills its monitor with no frame ("borderless windowed" / "fullscreen windowed").</summary>
    Borderless,
    /// <summary>
    /// Exclusive fullscreen: the game owns the display. Nothing can be drawn over it without hooking the game,
    /// which MapleLite never does. Window capture may also fail; the platform then tries a monitor capture.
    /// </summary>
    ExclusiveFullscreen,
    Minimized,
}

/// <summary>
/// The game window as the OS reports it. Bounds and Client are in screen pixels (physical, not scaled).
/// Handle is the OS window id: an HWND on Windows, a CGWindowID on macOS, an X11 window id on Linux.
/// </summary>
public sealed record GameWindow(nint Handle, int ProcessId, string ProcessName, string Title,
    Region Bounds, Region Client, WindowMode Mode, double Scale);

/// <summary>Which window to follow: by process name, or a specific process the user picked.</summary>
public sealed record GameTarget(string ProcessName = "MapleStory", int? ProcessId = null);

public sealed record WindowCandidate(nint Handle, int ProcessId, string ProcessName, string Title);

public interface IGameWindowFinder
{
    GameWindow? Find(GameTarget target);
    /// <summary>Re-reads position, size and mode; null when the window has closed.</summary>
    GameWindow? Refresh(GameWindow window);
    /// <summary>Visible windows the user can pick from, likeliest game windows first.</summary>
    IReadOnlyList<WindowCandidate> ListCandidates();
}

/// <summary>Reads pictures of windows. Never reads game memory, never injects, never sends input.</summary>
public interface IWindowCapture : IDisposable
{
    /// <summary>The game's client area (no title bar or borders), at physical resolution.</summary>
    Task<Frame> CaptureClientAsync(GameWindow window, CancellationToken ct);
    /// <summary>Any other top-level window, such as a browser tab with a price site.</summary>
    Task<Frame> CaptureWindowAsync(nint handle, CancellationToken ct);
}

public interface ICursor
{
    /// <summary>The mouse position inside the game's client area, or null when unknown.</summary>
    (int X, int Y)? PositionIn(GameWindow window);
}

public interface IWindowList
{
    IReadOnlyList<WindowCandidate> TopLevelWindows();
}

public interface IClipboardImage
{
    Task SetPngAsync(byte[] png);
}

public enum PermissionState { Granted, Denied, NotRequired, Unknown }

/// <summary>Everything OS-specific the host needs. One implementation per OS.</summary>
public interface IPlatform : IDisposable
{
    /// <summary>"Windows", "macOS" or "Linux".</summary>
    string Name { get; }
    IGameWindowFinder Finder { get; }
    IWindowCapture Capture { get; }
    ICursor Cursor { get; }
    IWindowList Windows { get; }
    /// <summary>Null when the OS has no recognizer available.</summary>
    IOcrEngine? Ocr { get; }
    /// <summary>Null when not implemented yet; the host falls back to saving a file.</summary>
    IClipboardImage? Clipboard { get; }
    /// <summary>macOS needs Screen Recording permission; Wayland needs a portal grant; Windows needs nothing.</summary>
    PermissionState ScreenCapturePermission();
    /// <summary>Human-readable notes about limits on this OS, shown in the host's status page.</summary>
    IReadOnlyList<string> Notes { get; }
}

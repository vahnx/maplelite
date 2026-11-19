using System.Diagnostics;
using MapleLite.Core.Platform;

namespace MapleLite.Platform.Windows;

/// <summary>Finds and follows the game window. Reads window state only; never opens the game process.</summary>
internal sealed class WindowsFinder : IGameWindowFinder, IWindowList, ICursor
{
    static readonly int OwnPid = Environment.ProcessId;

    public GameWindow? Find(GameTarget target)
    {
        try
        {
            if (target.ProcessId is { } pid)
            {
                using var p = Process.GetProcessById(pid);
                return p.MainWindowHandle != 0 ? Describe(p.MainWindowHandle, p.Id, p.ProcessName) : null;
            }
            foreach (var p in Process.GetProcessesByName(target.ProcessName))
            {
                using (p)
                    if (p.MainWindowHandle != 0) return Describe(p.MainWindowHandle, p.Id, p.ProcessName);
            }
        }
        catch (ArgumentException) { /* the picked process has exited */ }
        catch (InvalidOperationException) { }
        return null;
    }

    public GameWindow? Refresh(GameWindow w) =>
        Win32.IsWindow(w.Handle) ? Describe(w.Handle, w.ProcessId, w.ProcessName) : null;

    internal static GameWindow? Describe(nint h, int pid, string processName)
    {
        var bounds = Win32.Bounds(h);
        var client = Win32.Client(h);
        if (bounds == null || client == null) return null;
        double scale = Math.Max(1, Win32.GetDpiForWindow(h)) / 96.0;
        return new GameWindow(h, pid, processName, Win32.Title(h), bounds.Value, client.Value, ModeOf(h, client.Value), scale);
    }

    /// <summary>
    /// Windowed: has a title bar or doesn't cover its monitor. Borderless: covers its monitor without a frame.
    /// Exclusive fullscreen: covers its monitor, is in front, and Windows reports a Direct3D fullscreen app running.
    /// </summary>
    internal static WindowMode ModeOf(nint h, Core.Imaging.Region client)
    {
        if (Win32.IsIconic(h)) return WindowMode.Minimized;
        var monitor = Win32.Monitor(h);
        long style = Win32.GetWindowLongPtr(h, Win32.GWL_STYLE);
        bool framed = (style & Win32.WS_CAPTION) == Win32.WS_CAPTION;
        bool covers = monitor is { } m && client.X <= m.X && client.Y <= m.Y && client.Right >= m.Right && client.Bottom >= m.Bottom;
        if (framed || !covers) return WindowMode.Windowed;
        bool exclusive = Win32.GetForegroundWindow() == h
            && Win32.SHQueryUserNotificationState(out int state) >= 0 && state == Win32.QUNS_RUNNING_D3D_FULL_SCREEN;
        return exclusive ? WindowMode.ExclusiveFullscreen : WindowMode.Borderless;
    }

    public IReadOnlyList<WindowCandidate> ListCandidates()
    {
        var all = TopLevelWindows();
        return all.OrderByDescending(c => c.ProcessName.Contains("maple", StringComparison.OrdinalIgnoreCase) || c.Title.Contains("maple", StringComparison.OrdinalIgnoreCase))
                  .ThenBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<WindowCandidate> TopLevelWindows()
    {
        var names = new Dictionary<int, string>();
        var list = new List<WindowCandidate>();
        foreach (var h in Win32.TopLevel())
        {
            if (!Win32.IsWindowVisible(h) || Win32.Cloaked(h)) continue;
            if ((Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0) continue;
            var title = Win32.Title(h);
            if (title.Length == 0) continue;
            Win32.GetWindowThreadProcessId(h, out int pid);
            if (pid == OwnPid) continue;
            if (!names.TryGetValue(pid, out var name))
            {
                try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { name = "?"; }
                names[pid] = name;
            }
            list.Add(new WindowCandidate(h, pid, name, title) { Minimized = Win32.IsIconic(h) });
        }
        return list;
    }

    public (int X, int Y)? PositionIn(GameWindow window)
    {
        if (!Win32.GetCursorPos(out var p)) return null;
        // Only while the pointer is over the game itself: over another window (MapleLite, a browser) that happens to sit
        // where a game window is drawn, it isn't on the game, and readers waiting for it to move off would wait forever.
        if (Win32.GetAncestor(Win32.WindowFromPoint(p), Win32.GA_ROOT) != Win32.GetAncestor(window.Handle, Win32.GA_ROOT)) return null;
        if (!Win32.ScreenToClient(window.Handle, ref p)) return null;
        return (p.X, p.Y);
    }
}

using System.Runtime.InteropServices;
using System.Text;
using MapleLite.Core.Imaging;

namespace MapleLite.Platform.Windows;

/// <summary>The user32/dwmapi/shell32 calls MapleLite uses. All of them only read window state.</summary>
internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public Region ToRegion() => new(L, T, R - L, B - T); }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(nint h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint h, uint flags);
    public const uint GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint h, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(nint h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint h, int attribute, out RECT rect, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint h, int attribute, out int value, int size);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    public static string Title(nint h)
    {
        int n = GetWindowTextLength(h);
        if (n <= 0) return "";
        var sb = new StringBuilder(n + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>The window's visible bounds (without the invisible resize border Windows 10/11 adds).</summary>
    public static Region? Bounds(nint h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) >= 0) return r.ToRegion();
        return GetWindowRect(h, out r) ? r.ToRegion() : null;
    }

    /// <summary>The client area in screen pixels.</summary>
    public static Region? Client(nint h)
    {
        if (!GetClientRect(h, out var r)) return null;
        var p = new POINT();
        if (!ClientToScreen(h, ref p)) return null;
        return new Region(p.X, p.Y, r.R - r.L, r.B - r.T);
    }

    public static Region? Monitor(nint h)
    {
        var mon = MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return mon != 0 && GetMonitorInfo(mon, ref info) ? info.rcMonitor.ToRegion() : null;
    }

    public static bool Cloaked(nint h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, sizeof(int)) >= 0 && c != 0;

    public static List<nint> TopLevel()
    {
        var list = new List<nint>();
        EnumWindows((h, _) => { list.Add(h); return true; }, 0);
        return list;
    }
}

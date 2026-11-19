using System.Diagnostics;
using MapleLite.Core.Imaging;
using MapleLite.Core.Platform;
using MapleLite.Platform.Mac.Interop;

namespace MapleLite.Platform.Mac;

/// <summary>
/// Finds and follows the game window with CGWindowListCopyWindowInfo. Reads window state only.
///
/// Geometry: macOS places windows in points, while capture is in pixels. <see cref="GameWindow.Bounds"/> and
/// <see cref="GameWindow.Client"/> are the window's points times its display's backing scale, so they line up with
/// the captured picture (and the cursor, which <see cref="PositionIn"/> converts the same way).
/// Client is Bounds without the title bar; see <see cref="Titlebar"/>.
/// </summary>
internal sealed class MacFinder : IGameWindowFinder, IWindowList, ICursor
{
    static readonly int OwnPid = Environment.ProcessId;

    static readonly nint KNumber = Key("kCGWindowNumber"), KPid = Key("kCGWindowOwnerPID"), KOwner = Key("kCGWindowOwnerName"),
        KName = Key("kCGWindowName"), KBounds = Key("kCGWindowBounds"), KLayer = Key("kCGWindowLayer"),
        KOnscreen = Key("kCGWindowIsOnscreen"), KAlpha = Key("kCGWindowAlpha");

    static nint Key(string name) => ObjC.Constant(CG.CoreGraphics, name);

    /// <summary>One row of the window list.</summary>
    internal sealed record Info(uint Id, int Pid, string Owner, string Title, CGRect Points, int Layer, bool Onscreen, double Alpha);

    /// <summary>All windows (on screen or not), front to back.</summary>
    internal static List<Info> List(uint option = CG.kCGWindowListOptionAll | CG.kCGWindowListExcludeDesktopElements, uint relativeTo = 0)
    {
        var list = new List<Info>();
        var array = CG.CGWindowListCopyWindowInfo(option, relativeTo);
        if (array == 0) return list;
        try
        {
            long n = CG.CFArrayGetCount(array);
            for (long i = 0; i < n; i++)
            {
                var d = CG.CFArrayGetValueAtIndex(array, (nint)i);
                var bounds = CG.CFDictionaryGetValue(d, KBounds);
                if (bounds == 0 || !CG.CGRectMakeWithDictionaryRepresentation(bounds, out var r)) continue;
                list.Add(new Info((uint)(CG.Long(d, KNumber) ?? 0), (int)(CG.Long(d, KPid) ?? 0), CG.String(d, KOwner), CG.String(d, KName),
                    r, (int)(CG.Long(d, KLayer) ?? 0), CG.Bool(d, KOnscreen), CG.Double(d, KAlpha) ?? 1));
            }
        }
        finally { CG.CFRelease(array); }
        return list;
    }

    internal static Info? One(uint id) =>
        List(CG.kCGWindowListOptionIncludingWindow, id).FirstOrDefault(w => w.Id == id);

    /// <summary>Ordinary app windows: normal layer, visible, not tiny, not ours.</summary>
    static bool IsAppWindow(Info w) =>
        w.Layer == 0 && w.Alpha > 0 && w.Points.Width >= 100 && w.Points.Height >= 100 && w.Pid != OwnPid;

    static string ProcessName(int pid, string owner)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return owner; }
    }

    static bool Matches(Info w, string name) =>
        string.Equals(w.Owner, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ProcessName(w.Pid, w.Owner), name, StringComparison.OrdinalIgnoreCase);

    public GameWindow? Find(GameTarget target)
    {
        var windows = List().Where(IsAppWindow);
        windows = target.ProcessId is { } pid ? windows.Where(w => w.Pid == pid) : windows.Where(w => Matches(w, target.ProcessName));
        // A game can own small extra windows (launchers, dialogs): follow its biggest on-screen one.
        var best = windows.OrderByDescending(w => w.Onscreen).ThenByDescending(w => w.Points.Width * w.Points.Height).FirstOrDefault();
        return best == null ? null : Describe(best);
    }

    public GameWindow? Refresh(GameWindow window) =>
        One((uint)window.Handle) is { } w ? Describe(w) with { ProcessName = window.ProcessName } : null;

    internal static GameWindow Describe(Info w)
    {
        var display = Displays.For(w.Points);
        double scale = display.Scale;
        var bounds = ToPixels(w.Points, scale);
        bool fullscreen = Math.Abs(w.Points.X - display.Points.X) < 1 && Math.Abs(w.Points.Y - display.Points.Y) < 1
            && Math.Abs(w.Points.Width - display.Points.Width) < 1 && Math.Abs(w.Points.Height - display.Points.Height) < 1;
        int top = Titlebar.Height(w.Id, bounds.Width, bounds.Height, scale, fullscreen);
        var client = new Region(bounds.X, bounds.Y + top, bounds.Width, Math.Max(1, bounds.Height - top));
        // Off screen means minimized, hidden, or on another Space; none of those can be read until it's back.
        var mode = !w.Onscreen ? WindowMode.Minimized : fullscreen ? WindowMode.Borderless : WindowMode.Windowed;
        var title = w.Title.Length > 0 ? w.Title : w.Owner;
        return new GameWindow((nint)w.Id, w.Pid, ProcessName(w.Pid, w.Owner), title, bounds, client, mode, scale);
    }

    internal static Region ToPixels(CGRect r, double scale) =>
        new((int)Math.Round(r.X * scale), (int)Math.Round(r.Y * scale), (int)Math.Round(r.Width * scale), (int)Math.Round(r.Height * scale));

    public IReadOnlyList<WindowCandidate> ListCandidates() =>
        TopLevelWindows()
            .OrderByDescending(c => c.ProcessName.Contains("maple", StringComparison.OrdinalIgnoreCase) || c.Title.Contains("maple", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<WindowCandidate> TopLevelWindows()
    {
        var names = new Dictionary<int, string>();
        var list = new List<WindowCandidate>();
        foreach (var w in List())
        {
            if (!IsAppWindow(w)) continue;
            // Window titles need Screen Recording permission; without it only the app name is known.
            var title = w.Title.Length > 0 ? w.Title : w.Owner;
            if (title.Length == 0) continue;
            if (!names.TryGetValue(w.Pid, out var name)) names[w.Pid] = name = ProcessName(w.Pid, w.Owner);
            list.Add(new WindowCandidate((nint)w.Id, w.Pid, name, title) { Minimized = !w.Onscreen });
        }
        return list;
    }

    public (int X, int Y)? PositionIn(GameWindow window)
    {
        var ev = CG.CGEventCreate(0);
        if (ev == 0) return null;
        try
        {
            var p = CG.CGEventGetLocation(ev); // points, top-left origin, same space as window bounds
            // The captured picture is in game pixels (see GamePixels), so the cursor is too.
            int s = GamePixels.Factor(window.Scale);
            double x = (p.X * window.Scale - window.Client.X) / s, y = (p.Y * window.Scale - window.Client.Y) / s;
            return ((int)Math.Floor(x), (int)Math.Floor(y));
        }
        finally { CG.CFRelease(ev); }
    }
}

/// <summary>Active displays: their bounds in points and backing scale (2 on Retina).</summary>
internal static unsafe class Displays
{
    internal readonly record struct Display(uint Id, CGRect Points, double Scale);

    public static IReadOnlyList<Display> All()
    {
        var ids = stackalloc uint[16];
        if (CG.CGGetActiveDisplayList(16, ids, out var count) != 0) return [];
        var list = new List<Display>((int)count);
        for (int i = 0; i < count; i++)
        {
            var mode = CG.CGDisplayCopyDisplayMode(ids[i]);
            double scale = 1;
            if (mode != 0)
            {
                double pts = CG.CGDisplayModeGetWidth(mode), px = CG.CGDisplayModeGetPixelWidth(mode);
                if (pts > 0 && px > 0) scale = px / pts;
                CG.CGDisplayModeRelease(mode);
            }
            list.Add(new Display(ids[i], CG.CGDisplayBounds(ids[i]), scale));
        }
        return list;
    }

    /// <summary>The display holding most of <paramref name="r"/>.</summary>
    public static Display For(CGRect r)
    {
        var all = All();
        if (all.Count == 0) return new Display(0, r, 1);
        return all.OrderByDescending(d => Overlap(d.Points, r)).First();
    }

    static double Overlap(CGRect a, CGRect b)
    {
        double w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        double h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

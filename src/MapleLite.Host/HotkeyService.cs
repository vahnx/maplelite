using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Input;
using MapleLite.Sdk;

namespace MapleLite.Host;

/// <summary>
/// System-wide shortcuts for plugins (<c>context.Hotkeys</c>). On Windows each one is a RegisterHotKey on a thread of
/// MapleLite's own with its own message loop: no keyboard hook, nothing inside the game, no input sent anywhere.
/// The OS hands the combination to MapleLite instead of the active window. Other systems: not supported yet.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    const uint WM_HOTKEY = 0x0312, WM_APP = 0x8000, WM_QUIT = 0x0012;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    readonly Action<string> _log;
    readonly ConcurrentQueue<Action> _work = new();
    readonly Dictionary<int, Action> _handlers = new(); // loop thread only
    readonly ManualResetEventSlim _ready = new();
    Thread? _thread;
    uint _threadId;
    int _nextId;
    bool _disposed;

    public HotkeyService(Action<string> log) => _log = log;

    public bool Supported => OperatingSystem.IsWindows();

    public Registration Register(string owner, string name, string? gesture, Action pressed)
    {
        var r = new Registration(this, owner, name, pressed);
        r.Change(gesture);
        return r;
    }

    /// <summary>One shortcut. Changing or disposing it unregisters the old combination.</summary>
    internal sealed class Registration(HotkeyService service, string owner, string name, Action pressed) : IHotkey
    {
        int _id;
        public string Name { get; } = name;
        public string? Gesture { get; private set; }
        public bool Active => _id != 0;
        public string? Error { get; private set; }

        public bool Change(string? gesture)
        {
            lock (this)
            {
                if (_id != 0) { service.Unregister(_id); _id = 0; }
                Gesture = HotkeyText.Normalize(gesture);
                Error = null;
                if (Gesture == null)
                {
                    if (!string.IsNullOrWhiteSpace(gesture)) Error = $"\"{gesture}\" isn't a key combination.";
                    return string.IsNullOrWhiteSpace(gesture);
                }
                if (!service.Supported) { Error = "Hotkeys only work on Windows so far."; return false; }
                (_id, Error) = service.Add(Gesture, pressed);
                service._log($"hotkey [{owner}] {Name} = {Gesture}: {(_id != 0 ? "registered" : Error)}");
                return _id != 0;
            }
        }

        public void Dispose() { lock (this) { if (_id != 0) service.Unregister(_id); _id = 0; } }
    }

    (int Id, string? Error) Add(string gesture, Action pressed)
    {
        if (!HotkeyText.TryParse(gesture, out var mods, out var key)) return (0, "That isn't a key combination.");
        if (VirtualKey(key) is not { } vk) return (0, $"{key} can't be used as a hotkey.");
        uint m = MOD_NOREPEAT;
        if (mods.HasFlag(KeyModifiers.Alt)) m |= MOD_ALT;
        if (mods.HasFlag(KeyModifiers.Control)) m |= MOD_CONTROL;
        if (mods.HasFlag(KeyModifiers.Shift)) m |= MOD_SHIFT;
        if (mods.HasFlag(KeyModifiers.Meta)) m |= MOD_WIN;
        return OnLoop(() =>
        {
            int id = ++_nextId;
            if (RegisterHotKey(0, id, m, vk)) { _handlers[id] = pressed; return (id, (string?)null); }
            int err = Marshal.GetLastWin32Error();
            return (0, err == 1409 ? $"{gesture} is already used by another app (or another MapleLite shortcut)." : $"Windows refused {gesture} (error {err}).");
        }, (0, "The hotkey thread isn't responding."));
    }

    void Unregister(int id) => OnLoop(() => { UnregisterHotKey(0, id); _handlers.Remove(id); return true; }, false);

    /// <summary>Runs <paramref name="work"/> on the hotkey thread (hotkeys belong to the thread that registered them) and waits for it.</summary>
    T OnLoop<T>(Func<T> work, T timedOut)
    {
        if (_disposed || !Supported) return timedOut;
        EnsureThread();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() => { try { done.SetResult(work()); } catch (Exception e) { done.SetException(e); } });
        PostThreadMessage(_threadId, WM_APP, 0, 0);
        return done.Task.Wait(TimeSpan.FromSeconds(3)) ? done.Task.Result : timedOut;
    }

    void EnsureThread()
    {
        lock (_work)
        {
            if (_thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "MapleLite hotkeys" };
            _thread.Start();
        }
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    void Loop()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, 0, WM_APP, WM_APP, 0); // makes the thread's message queue, so posts can reach it
        _ready.Set();
        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.Message == WM_HOTKEY && _handlers.TryGetValue((int)msg.WParam, out var handler))
                ThreadPool.QueueUserWorkItem(_ => { try { handler(); } catch (Exception e) { _log("hotkey handler failed: " + e.Message); } });
            while (_work.TryDequeue(out var w)) w();
        }
        foreach (var id in _handlers.Keys) UnregisterHotKey(0, id);
        _handlers.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_thread != null) { PostThreadMessage(_threadId, WM_QUIT, 0, 0); _thread.Join(TimeSpan.FromSeconds(2)); }
    }

    /// <summary>The Windows virtual-key code for an Avalonia key, for the keys a shortcut can use.</summary>
    internal static uint? VirtualKey(Key k) => k switch
    {
        >= Key.A and <= Key.Z => (uint)('A' + (k - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)('0' + (k - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60u + (uint)(k - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70u + (uint)(k - Key.F1),
        Key.Space => 0x20, Key.PageUp => 0x21, Key.PageDown => 0x22, Key.End => 0x23, Key.Home => 0x24,
        Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
        Key.PrintScreen => 0x2C, Key.Insert => 0x2D, Key.Delete => 0x2E, Key.Pause => 0x13, Key.Scroll => 0x91,
        Key.Multiply => 0x6A, Key.Add => 0x6B, Key.Subtract => 0x6D, Key.Decimal => 0x6E, Key.Divide => 0x6F,
        Key.OemSemicolon => 0xBA, Key.OemPlus => 0xBB, Key.OemComma => 0xBC, Key.OemMinus => 0xBD, Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF, Key.OemTilde => 0xC0, Key.OemOpenBrackets => 0xDB, Key.OemPipe => 0xDC,
        Key.OemCloseBrackets => 0xDD, Key.OemQuotes => 0xDE,
        _ => null,
    };

    [StructLayout(LayoutKind.Sequential)]
    struct Msg { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(nint hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out Msg msg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PeekMessage(out Msg msg, nint hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, nuint w, nint l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}

/// <summary>A plugin's view of the hotkey service; everything it registers goes away when it stops.</summary>
internal sealed class PluginHotkeys(HotkeyService service, string owner, Func<IDisposable, IDisposable> track) : IHotkeys
{
    public bool Supported => service.Supported;
    public IHotkey Register(string name, string? gesture, Action pressed)
    {
        var r = service.Register(owner, name, gesture, pressed);
        track(r);
        return r;
    }
}

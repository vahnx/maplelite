using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using MapleLite.Core.Imaging;
using MapleLite.Core.Platform;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace MapleLite.Platform.Windows;

/// <summary>
/// Windows.Graphics.Capture: reads a window's own surface, even when other windows cover it.
/// The same API OBS and the Snipping Tool use. Nothing here opens the game process, reads its memory,
/// hooks its rendering, or sends it input. Ported from MesoSnap/MapleMemory's WindowCapture.
/// </summary>
internal sealed class WindowsCapture : IWindowCapture
{
    readonly object _lock = new();
    readonly Dictionary<(nint handle, bool monitor), WgcSession> _sessions = new();
    IDirect3DDevice? _device;

    IDirect3DDevice Device() { lock (_lock) return _device ??= WgcInterop.CreateDevice(); }

    WgcSession Session(nint handle, bool monitor)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue((handle, monitor), out var s)) return s;
            // Keep the game session and a few browser tabs; drop the oldest otherwise.
            if (_sessions.Count >= 6) { var old = _sessions.First(); old.Value.Dispose(); _sessions.Remove(old.Key); }
            s = new WgcSession(handle, monitor, Device());
            _sessions[(handle, monitor)] = s;
            return s;
        }
    }

    void Drop(nint handle, bool monitor)
    {
        lock (_lock)
            if (_sessions.Remove((handle, monitor), out var s)) s.Dispose();
    }

    public async Task<Frame> CaptureClientAsync(GameWindow window, CancellationToken ct)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows window capture is unavailable on this PC.");
        var h = window.Handle;
        if (Win32.IsIconic(h)) throw new InvalidOperationException("The game is minimized.");

        Frame whole;
        Region surface; // where the captured picture sits on screen
        try
        {
            whole = await CaptureAsync(h, false, ct).ConfigureAwait(false);
            surface = Win32.Bounds(h) ?? throw new InvalidOperationException("The game window closed during capture.");
        }
        catch (Exception) when (window.Mode == WindowMode.ExclusiveFullscreen && !ct.IsCancellationRequested)
        {
            // Exclusive fullscreen can starve window capture. The monitor is all game then, so read that instead.
            var mon = Win32.MonitorFromWindow(h, 2);
            whole = await CaptureAsync(mon, true, ct).ConfigureAwait(false);
            surface = Win32.Monitor(h) ?? throw new InvalidOperationException("The game's monitor disappeared.");
        }

        var client = Win32.Client(h) ?? throw new InvalidOperationException("The game window closed during capture.");
        if (client.Width < 200 || client.Height < 200) throw new InvalidOperationException("The game window is too small to read.");
        int left = client.X - surface.X, top = client.Y - surface.Y;
        if (whole.W == client.Width && whole.H == client.Height) { left = 0; top = 0; } // borderless surface is the client
        if (left < 0 || top < 0 || left + client.Width > whole.W || top + client.Height > whole.H)
            throw new InvalidOperationException("The game resized during capture.");
        return whole.Crop(new Region(left, top, client.Width, client.Height));
    }

    public Task<Frame> CaptureWindowAsync(nint handle, CancellationToken ct) => CaptureAsync(handle, false, ct);

    async Task<Frame> CaptureAsync(nint handle, bool monitor, CancellationToken ct)
    {
        var session = Session(handle, monitor);
        try { return await session.GrabAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { Drop(handle, monitor); throw; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
            try { _device?.Dispose(); } catch { }
            _device = null;
        }
    }
}

/// <summary>
/// One open capture session. Kept open between reads: setting one up each time is slow and makes
/// the capture border flash.
/// </summary>
internal sealed class WgcSession : IDisposable
{
    readonly nint _handle;
    readonly bool _monitor;
    readonly IDirect3DDevice _device;
    readonly SemaphoreSlim _gate = new(1, 1);
    global::Windows.Graphics.SizeInt32 _size;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    volatile TaskCompletionSource<bool>? _arrived;

    public WgcSession(nint handle, bool monitor, IDirect3DDevice device)
    {
        _handle = handle; _monitor = monitor; _device = device;
        Open();
    }

    void Open()
    {
        Close();
        var item = _monitor ? WgcInterop.ItemForMonitor(_handle) : WgcInterop.ItemForWindow(_handle);
        _size = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _pool.FrameArrived += (_, _) => _arrived?.TrySetResult(true);
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        WgcInterop.HideBorder(_session);
        _session.StartCapture();
    }

    void Close()
    {
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        _session = null; _pool = null;
    }

    public async Task<Frame> GrabAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The pool stops filling once its two buffers wait, so whatever waits is old:
            // drop it and take the next picture the game draws (or keep the newest if nothing changes).
            Direct3D11CaptureFrame? captured = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _arrived = signal;
                Direct3D11CaptureFrame? stale = null;
                while (_pool!.TryGetNextFrame() is { } waiting) { stale?.Dispose(); stale = waiting; }
                bool fresh = await Task.WhenAny(signal.Task, Task.Delay(stale == null ? 4000 : 400, ct)).ConfigureAwait(false) == signal.Task;
                ct.ThrowIfCancellationRequested();
                captured = fresh ? _pool.TryGetNextFrame() : null;
                if (captured != null) stale?.Dispose(); else captured = stale;
                if (captured == null) break;
                var s = captured.ContentSize;
                if (s.Width == _size.Width && s.Height == _size.Height) break;
                captured.Dispose(); captured = null; // resized: reopen at the new size
                Open();
            }
            if (captured == null) throw new TimeoutException("the window sent no picture. Is it minimized?");
            using (captured)
            using (var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(captured.Surface))
            {
                var frame = new Frame(bitmap.PixelWidth, bitmap.PixelHeight);
                var bytes = new byte[frame.Px.Length * 4];
                bitmap.CopyToBuffer(bytes.AsBuffer());
                Buffer.BlockCopy(bytes, 0, frame.Px, 0, bytes.Length);
                return frame;
            }
        }
        finally { _arrived = null; _gate.Release(); }
    }

    public void Dispose() { Close(); _gate.Dispose(); }
}

internal static class WgcInterop
{
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(nint window, ref Guid iid, out nint result);
        void CreateForMonitor(nint monitor, ref Guid iid, out nint result);
    }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(nint adapter, int driver, nint software, uint flags, nint levels, uint levelCount, uint version, out nint device, out int level, out nint context);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PutBool(nint self, byte value);

    public static GraphicsCaptureItem ItemForWindow(nint hwnd) => Item(hwnd, false);
    public static GraphicsCaptureItem ItemForMonitor(nint hmon) => Item(hmon, true);

    static GraphicsCaptureItem Item(nint handle, bool monitor)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var str));
        nint factory = 0, item = 0;
        object? factoryObject = null;
        try
        {
            var interopId = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(str, ref interopId, out factory));
            factoryObject = Marshal.GetObjectForIUnknown(factory);
            var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            var interop = (IGraphicsCaptureItemInterop)factoryObject;
            if (monitor) interop.CreateForMonitor(handle, ref itemId, out item);
            else interop.CreateForWindow(handle, ref itemId, out item);
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factoryObject != null) Marshal.ReleaseComObject(factoryObject);
            if (factory != 0) Marshal.Release(factory);
            WindowsDeleteString(str);
        }
    }

    public static IDirect3DDevice CreateDevice()
    {
        nint native = 0, context = 0, dxgi = 0, abi = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out native, out _, out context));
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, ref iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out abi));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            if (abi != 0) Marshal.Release(abi);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (native != 0) Marshal.Release(native);
        }
    }

    /// <summary>Asks Windows 11 not to draw its yellow border around the captured window. Ignored where unsupported.</summary>
    public static void HideBorder(GraphicsCaptureSession session)
    {
        nint unknown = 0, session3 = 0;
        try
        {
            unknown = WinRT.MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
            var iid = new Guid("f2cdd966-22ae-5ea1-9596-3a289344c3be"); // IGraphicsCaptureSession3
            if (Marshal.QueryInterface(unknown, ref iid, out session3) < 0) return;
            var put = Marshal.ReadIntPtr(Marshal.ReadIntPtr(session3), 7 * IntPtr.Size); // put_IsBorderRequired
            Marshal.GetDelegateForFunctionPointer<PutBool>(put)(session3, 0);
        }
        catch { }
        finally
        {
            if (session3 != 0) Marshal.Release(session3);
            if (unknown != 0) Marshal.Release(unknown);
        }
    }
}

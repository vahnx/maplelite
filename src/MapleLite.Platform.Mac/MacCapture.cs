using System.Collections.Concurrent;
using MapleLite.Core.Imaging;
using MapleLite.Core.Platform;
using MapleLite.Platform.Mac.Interop;
using static MapleLite.Platform.Mac.Interop.ObjC;

namespace MapleLite.Platform.Mac;

/// <summary>
/// Reads window pictures with ScreenCaptureKit (macOS 12.3+): one SCStream per window, filtered to that window
/// alone, so covered windows still read. The same API QuickTime and OBS use; nothing here touches the game process.
/// Older systems, or a stream that won't start, fall back to CGWindowListCreateImage (slower, one picture per call).
/// Needs Screen Recording permission.
/// </summary>
internal sealed class MacCapture : IWindowCapture
{
    readonly Dictionary<uint, StreamSession> _sessions = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    bool _askedPermission;
    DateTime _streamRetryAfter;
    string? _streamError;

    // The last crop, so the same picture comes back as the same Frame (the shared OCR caches by Frame.Id).
    Frame? _lastWhole, _lastClient;

    static readonly bool StreamsAvailable =
        OperatingSystem.IsMacOSVersionAtLeast(12, 3) && TryLoad(ObjC.ScreenCaptureKit) && Class("SCStream") != 0;

    /// <summary>The reason capture runs on the slower fallback, when it does.</summary>
    public string? FallbackReason => !StreamsAvailable ? "ScreenCaptureKit needs macOS 12.3 or later." : _streamError;

    void EnsurePermission()
    {
        if (CG.CGPreflightScreenCaptureAccess()) return;
        if (!_askedPermission) { _askedPermission = true; CG.CGRequestScreenCaptureAccess(); }
        throw new InvalidOperationException("MapleLite needs Screen Recording permission. Allow it in System Settings > Privacy & Security > "
            + "Screen Recording (for the app you started MapleLite from, such as Terminal), then restart MapleLite.");
    }

    public async Task<Frame> CaptureClientAsync(GameWindow window, CancellationToken ct)
    {
        EnsurePermission();
        uint id = (uint)window.Handle;
        var info = MacFinder.One(id) ?? throw new InvalidOperationException("The game window closed.");
        if (!info.Onscreen) throw new InvalidOperationException("The game is minimized or on another Space.");
        var now = MacFinder.Describe(info);

        var whole = await GrabAsync(id, now.Bounds.Width, now.Bounds.Height, ct).ConfigureAwait(false);
        if (ReferenceEquals(whole, _lastWhole) && _lastClient != null) return _lastClient;

        int top = Titlebar.Resolve(id, whole, now.Scale, now.Mode == WindowMode.Borderless);
        if (whole.W < 200 || whole.H - top < 200) throw new InvalidOperationException("The game window is too small to read.");
        var client = top == 0 ? whole : whole.Crop(new Region(0, top, whole.W, whole.H - top));
        _lastWhole = whole; _lastClient = client;
        return client;
    }

    public async Task<Frame> CaptureWindowAsync(nint handle, CancellationToken ct)
    {
        EnsurePermission();
        uint id = (uint)handle;
        var info = MacFinder.One(id) ?? throw new InvalidOperationException("That window closed.");
        var px = MacFinder.Describe(info).Bounds;
        return await GrabAsync(id, px.Width, px.Height, ct).ConfigureAwait(false);
    }

    async Task<Frame> GrabAsync(uint id, int w, int h, CancellationToken ct)
    {
        if (StreamsAvailable && DateTime.UtcNow >= _streamRetryAfter)
        {
            try
            {
                var s = await SessionAsync(id, w, h, ct).ConfigureAwait(false);
                var f = await s.LatestAsync(ct).ConfigureAwait(false);
                _streamError = null;
                return f;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _streamError = e.Message;
                _streamRetryAfter = DateTime.UtcNow.AddSeconds(10);
                await DropAsync(id).ConfigureAwait(false);
            }
        }
        return Snapshot(id);
    }

    /// <summary>One picture with CGWindowListCreateImage: works back to old systems, but costs more per frame.</summary>
    Frame Snapshot(uint id)
    {
        var img = CG.CGWindowListCreateImage(CGRect.Null, CG.kCGWindowListOptionIncludingWindow, id,
            CG.kCGWindowImageBoundsIgnoreFraming | CG.kCGWindowImageBestResolution);
        if (img == 0) throw new InvalidOperationException("Couldn't capture the window." + (_streamError is { } e ? " ScreenCaptureKit: " + e : ""));
        try
        {
            if (CG.CGImageGetWidth(img) < 2 || CG.CGImageGetHeight(img) < 2) throw new InvalidOperationException("The window's picture came back empty.");
            return CG.ToFrame(img);
        }
        finally { CG.CGImageRelease(img); }
    }

    async Task<StreamSession> SessionAsync(uint id, int w, int h, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Streams nobody reads (a browser window a plugin looked at once) stop after a while.
            foreach (var idle in _sessions.Where(kv => kv.Key != id && DateTime.UtcNow - kv.Value.LastUsed > TimeSpan.FromSeconds(20)).ToList())
            {
                _sessions.Remove(idle.Key);
                idle.Value.Dispose();
            }
            if (_sessions.TryGetValue(id, out var s))
            {
                if (s.W == w && s.H == h) return s;
                _sessions.Remove(id); // the window was resized or moved to a display with another scale
                s.Dispose();
            }
            s = await StreamSession.StartAsync(id, w, h, ct).ConfigureAwait(false);
            _sessions[id] = s;
            return s;
        }
        finally { _gate.Release(); }
    }

    async Task DropAsync(uint id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_sessions.Remove(id, out var s)) s.Dispose(); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }

    /// <summary>A running SCStream for one window. Keeps only the newest pixel buffer; converts it when asked.</summary>
    sealed class StreamSession : IDisposable
    {
        const uint BGRA = 0x42475241; // kCVPixelFormatType_32BGRA
        static readonly ConcurrentDictionary<nint, StreamSession> ByOutput = new();
        static readonly nint OutputClass = MakeOutputClass();
        static readonly nint StatusKey = Constant(ObjC.ScreenCaptureKit, "SCStreamFrameInfoStatus");

        nint _stream, _output, _queue;
        readonly object _lock = new();
        nint _latest;
        long _seq, _convertedSeq = -1;
        Frame? _converted;
        readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _disposed;

        public int W { get; private init; }
        public int H { get; private init; }
        public DateTime LastUsed { get; private set; } = DateTime.UtcNow;

        public static async Task<StreamSession> StartAsync(uint id, int w, int h, CancellationToken ct)
        {
            var content = await ShareableContentAsync(ct).ConfigureAwait(false);
            nint window = 0;
            try
            {
                Pool(() =>
                {
                    var windows = Send(content, "windows");
                    for (int i = 0, n = Count(windows); i < n; i++)
                    {
                        var win = At(windows, i);
                        if ((uint)(Send(win, "windowID") & 0xffffffff) == id) { window = Retain(win); break; }
                    }
                    return 0;
                });
            }
            finally { Release(content); }
            if (window == 0) throw new InvalidOperationException("ScreenCaptureKit doesn't list the window.");

            var s = new StreamSession { W = w, H = h };
            try { Pool(() => { s.Create(window, w, h); return 0; }); }
            catch { s.Dispose(); throw; }
            finally { Release(window); }

            try
            {
                await s.StartCaptureAsync(ct).ConfigureAwait(false);
                return s;
            }
            catch { s.Dispose(); throw; }
        }

        static async Task<nint> ShareableContentAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = Blocks.Create("v24@?0@8@16", (content, error) =>
            {
                if (content == 0) { tcs.TrySetException(new InvalidOperationException(Describe(error))); return; }
                var kept = Retain(content);
                if (!tcs.TrySetResult(kept)) Release(kept); // nobody is waiting any more
            });
            Send(Class("SCShareableContent"), "getShareableContentExcludingDesktopWindows:onScreenWindowsOnly:completionHandler:", Bool(true), Bool(false), block);
            try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (TimeoutException) { tcs.TrySetCanceled(); throw new InvalidOperationException("ScreenCaptureKit didn't answer."); }
        }

        unsafe void Create(nint window, int w, int h)
        {
            var filter = Send(Send(Class("SCContentFilter"), "alloc"), "initWithDesktopIndependentWindow:", window);
            var config = New("SCStreamConfiguration");
            try
            {
                Send(config, "setWidth:", w);
                Send(config, "setHeight:", h);
                Send(config, "setPixelFormat:", (nint)BGRA);
                Send(config, "setShowsCursor:", Bool(false));
                SendTime(config, "setMinimumFrameInterval:", CMTime.Make(1, 60));
                Send(config, "setQueueDepth:", 5);
                if (Responds(config, "setIgnoreShadowsSingleWindow:")) Send(config, "setIgnoreShadowsSingleWindow:", Bool(true));
                if (Responds(config, "setColorSpaceName:")) Send(config, "setColorSpaceName:", CG.SrgbName);
                _stream = Send(Send(Class("SCStream"), "alloc"), "initWithFilter:configuration:delegate:", filter, config, 0);
            }
            finally { Release(filter); Release(config); }
            if (_stream == 0) throw new InvalidOperationException("ScreenCaptureKit wouldn't make a stream for the window.");

            _output = Send(Send(OutputClass, "alloc"), "init");
            ByOutput[_output] = this;
            _queue = CG.dispatch_queue_create("com.cheesyfrog.maplelite.capture", 0);
            nint error = 0;
            const nint Screen = 0; // SCStreamOutputTypeScreen
            if (!IsYes(Send(_stream, "addStreamOutput:type:sampleHandlerQueue:error:", _output, Screen, _queue, (nint)(&error))))
                throw new InvalidOperationException("ScreenCaptureKit: " + Describe(error));
        }

        async Task StartCaptureAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = Blocks.Create("v16@?0@8", (error, _) =>
            {
                if (error != 0) tcs.TrySetException(new InvalidOperationException(Describe(error)));
                else tcs.TrySetResult();
            });
            Send(_stream, "startCaptureWithCompletionHandler:", block);
            try { await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new InvalidOperationException("The capture stream didn't start."); }
        }

        public async Task<Frame> LatestAsync(CancellationToken ct)
        {
            LastUsed = DateTime.UtcNow;
            // ScreenCaptureKit sends a frame only when the window changes, so after the first one the newest is current.
            try { await _first.Task.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new InvalidOperationException("The window's picture didn't arrive."); }

            nint pb; long seq;
            lock (_lock)
            {
                if (_seq == _convertedSeq && _converted != null) return _converted;
                pb = _latest; seq = _seq;
                CG.CVPixelBufferRetain(pb);
            }
            try
            {
                var f = CG.ToFrameFromPixelBuffer(pb);
                lock (_lock) { if (seq > _convertedSeq) { _converted = f; _convertedSeq = seq; } }
                return f;
            }
            finally { CG.CVPixelBufferRelease(pb); }
        }

        void Take(nint sampleBuffer)
        {
            if (!Complete(sampleBuffer)) return; // idle/blank/suspended frames carry no new picture
            var pb = CG.CMSampleBufferGetImageBuffer(sampleBuffer);
            if (pb == 0) return;
            CG.CVPixelBufferRetain(pb);
            nint old;
            lock (_lock) { old = _latest; _latest = pb; _seq++; }
            if (old != 0) CG.CVPixelBufferRelease(old);
            _first.TrySetResult();
        }

        static bool Complete(nint sampleBuffer)
        {
            var attachments = CG.CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, 0);
            if (attachments == 0 || CG.CFArrayGetCount(attachments) == 0) return true;
            var status = CG.Long(CG.CFArrayGetValueAtIndex(attachments, 0), StatusKey);
            return status is null or 0; // SCFrameStatusComplete
        }

        static unsafe nint MakeOutputClass()
        {
            const string name = "MapleLiteStreamOutput";
            var cls = objc_allocateClassPair(Class("NSObject"), name, 0);
            if (cls == 0) return Class(name); // already made (shouldn't happen: this runs once)
            class_addMethod(cls, Sel("stream:didOutputSampleBuffer:ofType:"),
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&OnSample, "v@:@^{opaqueCMSampleBuffer=}q");
            var protocol = objc_getProtocol("SCStreamOutput");
            if (protocol != 0) class_addProtocol(cls, protocol);
            objc_registerClassPair(cls);
            return cls;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        static void OnSample(nint self, nint cmd, nint stream, nint sampleBuffer, nint type)
        {
            try
            {
                if (type == 0 && ByOutput.TryGetValue(self, out var s)) s.Take(sampleBuffer);
            }
            catch { /* never unwind into ScreenCaptureKit */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _first.TrySetException(new ObjectDisposedException(nameof(StreamSession)));
            _first.Task.Exception?.Handle(_ => true);
            ByOutput.TryRemove(_output, out _);
            nint stream = _stream, output = _output, queue = _queue;
            if (stream != 0)
            {
                // Release everything once the stream has really stopped, so no frame arrives at a freed object.
                var block = Blocks.Create("v16@?0@8", (_, _) =>
                {
                    Release(stream); Release(output);
                    if (queue != 0) CG.dispatch_release(queue);
                });
                Send(stream, "stopCaptureWithCompletionHandler:", block);
            }
            else
            {
                Release(output);
                if (queue != 0) CG.dispatch_release(queue);
            }
            lock (_lock) { if (_latest != 0) { CG.CVPixelBufferRelease(_latest); _latest = 0; } }
        }
    }
}

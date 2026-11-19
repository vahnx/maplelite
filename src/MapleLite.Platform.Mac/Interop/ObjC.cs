using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MapleLite.Platform.Mac.Interop;

/// <summary>
/// Just enough of the Objective-C runtime to call Apple's frameworks from plain net8.0 (no macOS workload),
/// so this project still builds on Windows. Every message send goes through objc_msgSend cast to the exact
/// native signature, which is how the ABI expects it on both arm64 and x86_64.
/// </summary>
internal static unsafe class ObjC
{
    const string LibObjC = "/usr/lib/libobjc.A.dylib";
    public const string LibSystem = "/usr/lib/libSystem.B.dylib";
    public const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    public const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    public const string Vision = "/System/Library/Frameworks/Vision.framework/Vision";
    public const string ScreenCaptureKit = "/System/Library/Frameworks/ScreenCaptureKit.framework/ScreenCaptureKit";

    static readonly nint ObjCLib = NativeLibrary.Load(LibObjC);
    static readonly nint MsgSend = NativeLibrary.GetExport(ObjCLib, "objc_msgSend");
    // On x86_64 a struct bigger than 16 bytes (CGRect) comes back through a hidden pointer, which needs the _stret entry.
    // arm64 returns CGRect in registers through plain objc_msgSend and has no _stret export.
    static readonly nint MsgSendRect = RuntimeInformation.ProcessArchitecture == Architecture.X64
        && NativeLibrary.TryGetExport(ObjCLib, "objc_msgSend_stret", out var stret) ? stret : MsgSend;

    static readonly ConcurrentDictionary<string, nint> Libs = new();
    static readonly ConcurrentDictionary<string, nint> Selectors = new();

    [DllImport(LibObjC)] static extern nint objc_getClass(string name);
    [DllImport(LibObjC)] static extern nint sel_registerName(string name);
    [DllImport(LibObjC)] public static extern nint objc_getProtocol(string name);
    [DllImport(LibObjC)] public static extern nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);
    [DllImport(LibObjC)] public static extern void objc_registerClassPair(nint cls);
    [DllImport(LibObjC)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);
    [DllImport(LibObjC)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool class_addProtocol(nint cls, nint protocol);
    [DllImport(LibObjC)] static extern nint objc_autoreleasePoolPush();
    [DllImport(LibObjC)] static extern void objc_autoreleasePoolPop(nint pool);

    /// <summary>Loads a framework once (dlopen) so its classes and exported constants exist.</summary>
    public static nint Load(string path) => Libs.GetOrAdd(path, p => NativeLibrary.Load(p));

    public static bool TryLoad(string path)
    {
        try { Load(path); return true; } catch (DllNotFoundException) { return false; }
    }

    /// <summary>The value of an exported global such as <c>NSString *const SCStreamFrameInfoStatus</c>.</summary>
    public static nint Constant(string library, string symbol) =>
        NativeLibrary.TryGetExport(Load(library), symbol, out var p) ? Marshal.ReadIntPtr(p) : 0;

    public static nint Export(string library, string symbol) =>
        NativeLibrary.TryGetExport(Load(library), symbol, out var p) ? p : 0;

    public static nint Class(string name) => objc_getClass(name);
    public static nint Sel(string name) => Selectors.GetOrAdd(name, sel_registerName);

    // ---- message sends, by native signature ----
    public static nint Send(nint o, string sel) => ((delegate* unmanaged<nint, nint, nint>)MsgSend)(o, Sel(sel));
    public static nint Send(nint o, string sel, nint a) => ((delegate* unmanaged<nint, nint, nint, nint>)MsgSend)(o, Sel(sel), a);
    public static nint Send(nint o, string sel, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)MsgSend)(o, Sel(sel), a, b);
    public static nint Send(nint o, string sel, nint a, nint b, nint c) => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MsgSend)(o, Sel(sel), a, b, c);
    public static nint Send(nint o, string sel, nint a, nint b, nint c, nint d) => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)MsgSend)(o, Sel(sel), a, b, c, d);
    public static void SendFloat(nint o, string sel, float a) => ((delegate* unmanaged<nint, nint, float, void>)MsgSend)(o, Sel(sel), a);
    public static void SendTime(nint o, string sel, CMTime a) => ((delegate* unmanaged<nint, nint, CMTime, void>)MsgSend)(o, Sel(sel), a);
    public static nint SendRange(nint o, string sel, NSRange a, nint b) => ((delegate* unmanaged<nint, nint, NSRange, nint, nint>)MsgSend)(o, Sel(sel), a, b);
    public static CGRect SendRect(nint o, string sel) => ((delegate* unmanaged<nint, nint, CGRect>)MsgSendRect)(o, Sel(sel));

    /// <summary>BOOL results: only the low byte is defined.</summary>
    public static bool IsYes(nint result) => (result & 0xff) != 0;
    public static nint Bool(bool b) => b ? 1 : 0;

    public static bool Responds(nint o, string sel) => o != 0 && IsYes(Send(o, "respondsToSelector:", Sel(sel)));

    public static nint New(string cls) => Send(Send(Class(cls), "alloc"), "init");
    public static void Release(nint o) { if (o != 0) Send(o, "release"); }
    public static nint Retain(nint o) => o == 0 ? 0 : Send(o, "retain");

    /// <summary>An autoreleased NSString.</summary>
    public static nint Str(string s)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(s);
        try { return Send(Class("NSString"), "stringWithUTF8String:", utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    public static string? ToStr(nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String"));

    public static nint ArrayOf(nint item) => Send(Class("NSArray"), "arrayWithObject:", item);
    public static int Count(nint nsArray) => nsArray == 0 ? 0 : (int)Send(nsArray, "count");
    public static nint At(nint nsArray, int i) => Send(nsArray, "objectAtIndex:", i);

    public static string Describe(nint nsError) =>
        nsError == 0 ? "unknown error" : ToStr(Send(nsError, "localizedDescription")) ?? "unknown error";

    /// <summary>Wraps work in an autorelease pool: thread-pool threads have none, so autoreleased objects would leak.</summary>
    public static T Pool<T>(Func<T> work)
    {
        var pool = objc_autoreleasePoolPush();
        try { return work(); }
        finally { objc_autoreleasePoolPop(pool); }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint { public double X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize { public double Width, Height; }

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public double X, Y, Width, Height;
    public CGRect(double x, double y, double w, double h) { X = x; Y = y; Width = w; Height = h; }
    public static readonly CGRect Null = new(double.PositiveInfinity, double.PositiveInfinity, 0, 0);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NSRange { public nuint Location, Length; public NSRange(int loc, int len) { Location = (nuint)loc; Length = (nuint)len; } }

[StructLayout(LayoutKind.Sequential)]
internal struct CMTime
{
    public long Value; public int Timescale; public uint Flags; public long Epoch;
    public static CMTime Make(long value, int timescale) => new() { Value = value, Timescale = timescale, Flags = 1 /* valid */ };
}

/// <summary>
/// An Objective-C block made by hand (the Blocks ABI), for completion handlers.
/// <see cref="Pass"/> builds it as a stack block, hands it to the API, and frees it when the call returns.
/// An async API keeps it with Block_copy, which makes a heap copy that the block runtime reference-counts;
/// the copy helper gives that copy its own handle to the callback and the dispose helper frees it once the
/// API has released the block. The block never frees itself while running: the caller still releases it
/// after the call returns, so freeing it inside Invoke made ScreenCaptureKit release freed memory and crash.
/// The block's arguments arrive as raw pointers; arguments the API doesn't pass are ignored.
/// </summary>
internal static unsafe class Blocks
{
    [StructLayout(LayoutKind.Sequential)]
    struct Literal { public nint Isa; public int Flags; public int Reserved; public nint Invoke; public nint Descriptor; public nint Context; }

    [StructLayout(LayoutKind.Sequential)]
    struct Descriptor { public nuint Reserved; public nuint Size; public nint Copy; public nint Dispose; public nint Signature; }

    const int HasCopyDispose = 1 << 25, HasSignature = 1 << 30;
    static readonly nint StackIsa = ObjC.Export(ObjC.LibSystem, "_NSConcreteStackBlock");
    static readonly ConcurrentDictionary<string, nint> Descriptors = new();

    /// <summary>Makes the block, passes it to <paramref name="send"/> (which hands it to the API), then frees this copy.</summary>
    /// <param name="signature">Objective-C type encoding, e.g. "v16@?0@8" for (NSError*), "v24@?0@8@16" for (id, NSError*).</param>
    public static void Pass(string signature, Action<nint, nint> callback, Action<nint> send)
    {
        var desc = Descriptors.GetOrAdd(signature, s =>
        {
            var d = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
            d->Size = (nuint)sizeof(Literal);
            d->Copy = (nint)(delegate* unmanaged<nint, nint, void>)&CopyHelper;
            d->Dispose = (nint)(delegate* unmanaged<nint, void>)&DisposeHelper;
            d->Signature = Marshal.StringToCoTaskMemUTF8(s); // lives for the process
            return (nint)d;
        });
        var b = (Literal*)NativeMemory.AllocZeroed((nuint)sizeof(Literal));
        b->Isa = StackIsa;
        b->Flags = HasCopyDispose | HasSignature;
        b->Invoke = (nint)(delegate* unmanaged<nint, nint, nint, void>)&Invoke;
        b->Descriptor = desc;
        b->Context = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
        try { send((nint)b); }
        finally
        {
            GCHandle.FromIntPtr(b->Context).Free();
            NativeMemory.Free(b);
        }
    }

    [UnmanagedCallersOnly]
    static void Invoke(nint block, nint a, nint b)
    {
        try { ((Action<nint, nint>)GCHandle.FromIntPtr(((Literal*)block)->Context).Target!)(a, b); }
        catch { /* an exception must never unwind into native code */ }
    }

    /// <summary>Block_copy moved the literal to the heap: give the copy its own handle to the same callback.</summary>
    [UnmanagedCallersOnly]
    static void CopyHelper(nint dst, nint src)
    {
        var target = GCHandle.FromIntPtr(((Literal*)src)->Context).Target;
        ((Literal*)dst)->Context = GCHandle.ToIntPtr(GCHandle.Alloc(target));
    }

    /// <summary>The last reference to a heap copy went away.</summary>
    [UnmanagedCallersOnly]
    static void DisposeHelper(nint block)
    {
        var lit = (Literal*)block;
        if (lit->Context != 0) { GCHandle.FromIntPtr(lit->Context).Free(); lit->Context = 0; }
    }
}

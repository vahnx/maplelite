using System.Runtime.InteropServices;
using MapleLite.Core.Imaging;

namespace MapleLite.Platform.Mac.Interop;

/// <summary>CoreFoundation, CoreGraphics, CoreMedia, CoreVideo and libdispatch calls, plus CGImage ↔ Frame.</summary>
internal static unsafe class CG
{
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

    // ---- CoreFoundation ----
    [DllImport(CoreFoundation)] public static extern void CFRelease(nint cf);
    [DllImport(CoreFoundation)] public static extern nint CFRetain(nint cf);
    [DllImport(CoreFoundation)] public static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)] public static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CoreFoundation)] public static extern nint CFDictionaryGetValue(nint dict, nint key);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)] static extern bool CFNumberGetValue(nint number, nint type, out long value);
    [DllImport(CoreFoundation, EntryPoint = "CFNumberGetValue")] [return: MarshalAs(UnmanagedType.U1)] static extern bool CFNumberGetDouble(nint number, nint type, out double value);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)] static extern bool CFBooleanGetValue(nint boolean);
    [DllImport(CoreFoundation)] static extern nint CFStringGetLength(nint str);
    [DllImport(CoreFoundation)] static extern void CFStringGetCharacters(nint str, CFRange range, char* buffer);
    [DllImport(CoreFoundation)] static extern nint CFGetTypeID(nint cf);
    [DllImport(CoreFoundation)] static extern nint CFStringGetTypeID();

    [StructLayout(LayoutKind.Sequential)] struct CFRange { public nint Location, Length; }

    const nint kCFNumberSInt64Type = 4, kCFNumberDoubleType = 13;

    public static long? Long(nint dict, nint key)
    {
        var v = dict == 0 || key == 0 ? 0 : CFDictionaryGetValue(dict, key);
        return v != 0 && CFNumberGetValue(v, kCFNumberSInt64Type, out var n) ? n : null;
    }

    public static double? Double(nint dict, nint key)
    {
        var v = dict == 0 || key == 0 ? 0 : CFDictionaryGetValue(dict, key);
        return v != 0 && CFNumberGetDouble(v, kCFNumberDoubleType, out var n) ? n : null;
    }

    public static bool Bool(nint dict, nint key)
    {
        var v = dict == 0 || key == 0 ? 0 : CFDictionaryGetValue(dict, key);
        return v != 0 && CFBooleanGetValue(v);
    }

    public static string String(nint dict, nint key)
    {
        var v = dict == 0 || key == 0 ? 0 : CFDictionaryGetValue(dict, key);
        return v != 0 && CFGetTypeID(v) == CFStringGetTypeID() ? ToString(v) : "";
    }

    public static string ToString(nint cfString)
    {
        int len = (int)CFStringGetLength(cfString);
        if (len == 0) return "";
        var buf = new char[len];
        fixed (char* p = buf) CFStringGetCharacters(cfString, new CFRange { Location = 0, Length = len }, p);
        return new string(buf);
    }

    // ---- CoreGraphics: windows, displays, cursor, permission ----
    public const uint kCGWindowListOptionAll = 0, kCGWindowListOptionOnScreenOnly = 1,
        kCGWindowListOptionIncludingWindow = 8, kCGWindowListExcludeDesktopElements = 16;
    public const uint kCGWindowImageBoundsIgnoreFraming = 1, kCGWindowImageBestResolution = 8;

    [DllImport(CoreGraphics)] public static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CGRectMakeWithDictionaryRepresentation(nint dict, out CGRect rect);
    [DllImport(CoreGraphics)] public static extern int CGGetActiveDisplayList(uint max, uint* displays, out uint count);
    [DllImport(CoreGraphics)] public static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CoreGraphics)] public static extern nint CGDisplayCopyDisplayMode(uint display);
    [DllImport(CoreGraphics)] public static extern nuint CGDisplayModeGetWidth(nint mode);
    [DllImport(CoreGraphics)] public static extern nuint CGDisplayModeGetPixelWidth(nint mode);
    [DllImport(CoreGraphics)] public static extern void CGDisplayModeRelease(nint mode);
    [DllImport(CoreGraphics)] public static extern nint CGEventCreate(nint source);
    [DllImport(CoreGraphics)] public static extern CGPoint CGEventGetLocation(nint ev);
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CGRequestScreenCaptureAccess();
    [DllImport(CoreGraphics)] public static extern nint CGWindowListCreateImage(CGRect bounds, uint listOption, uint windowId, uint imageOption);

    // ---- CoreGraphics: bitmaps ----
    const uint kCGImageAlphaNoneSkipFirst = 6, kCGBitmapByteOrder32Little = 2 << 12;
    /// <summary>B,G,R,X in memory: the same layout as <see cref="Frame"/>.</summary>
    const uint BgrxLayout = kCGImageAlphaNoneSkipFirst | kCGBitmapByteOrder32Little;

    [DllImport(CoreGraphics)] static extern nint CGColorSpaceCreateWithName(nint name);
    [DllImport(CoreGraphics)] static extern void CGColorSpaceRelease(nint cs);
    [DllImport(CoreGraphics)] static extern nint CGBitmapContextCreate(void* data, nuint w, nuint h, nuint bitsPerComponent, nuint bytesPerRow, nint colorSpace, uint bitmapInfo);
    [DllImport(CoreGraphics)] static extern nint CGBitmapContextCreateImage(nint ctx);
    [DllImport(CoreGraphics)] static extern void CGContextDrawImage(nint ctx, CGRect rect, nint image);
    [DllImport(CoreGraphics)] static extern void CGContextRelease(nint ctx);
    [DllImport(CoreGraphics)] public static extern nuint CGImageGetWidth(nint image);
    [DllImport(CoreGraphics)] public static extern nuint CGImageGetHeight(nint image);
    [DllImport(CoreGraphics)] public static extern void CGImageRelease(nint image);

    /// <summary>sRGB, so colors match what Windows capture reports and the plugins' color checks keep working.</summary>
    public static nint SrgbName => ObjC.Constant(CoreGraphics, "kCGColorSpaceSRGB");

    static nint Srgb() => CGColorSpaceCreateWithName(SrgbName);

    /// <summary>Draws a CGImage into a new Frame, converted to sRGB.</summary>
    public static Frame ToFrame(nint image)
    {
        int w = (int)CGImageGetWidth(image), h = (int)CGImageGetHeight(image);
        var f = new Frame(w, h);
        var cs = Srgb();
        try
        {
            fixed (int* px = f.Px)
            {
                var ctx = CGBitmapContextCreate(px, (nuint)w, (nuint)h, 8, (nuint)(w * 4), cs, BgrxLayout);
                if (ctx == 0) throw new InvalidOperationException("Couldn't make a bitmap to read the picture into.");
                CGContextDrawImage(ctx, new CGRect(0, 0, w, h), image);
                CGContextRelease(ctx);
            }
        }
        finally { CGColorSpaceRelease(cs); }
        OpaqueAlpha(f.Px);
        return f;
    }

    /// <summary>
    /// A CGImage over the frame's pixels. The image may read the pixels lazily, so <paramref name="pinned"/>
    /// must stay pinned until the image is released.
    /// </summary>
    public static nint ToImage(Frame f, int* pinned)
    {
        var cs = Srgb();
        try
        {
            var ctx = CGBitmapContextCreate(pinned, (nuint)f.W, (nuint)f.H, 8, (nuint)(f.W * 4), cs, BgrxLayout);
            if (ctx == 0) throw new InvalidOperationException("Couldn't make a bitmap from the picture.");
            var img = CGBitmapContextCreateImage(ctx);
            CGContextRelease(ctx);
            return img;
        }
        finally { CGColorSpaceRelease(cs); }
    }

    static void OpaqueAlpha(int[] px)
    {
        for (int i = 0; i < px.Length; i++) px[i] |= unchecked((int)0xff000000);
    }

    // ---- CoreMedia / CoreVideo (ScreenCaptureKit frames) ----
    [DllImport(CoreMedia)] public static extern nint CMSampleBufferGetImageBuffer(nint sampleBuffer);
    [DllImport(CoreMedia)] public static extern nint CMSampleBufferGetSampleAttachmentsArray(nint sampleBuffer, byte createIfNecessary);
    [DllImport(CoreVideo)] public static extern nint CVPixelBufferRetain(nint pb);
    [DllImport(CoreVideo)] public static extern void CVPixelBufferRelease(nint pb);
    [DllImport(CoreVideo)] public static extern int CVPixelBufferLockBaseAddress(nint pb, ulong flags);
    [DllImport(CoreVideo)] public static extern int CVPixelBufferUnlockBaseAddress(nint pb, ulong flags);
    [DllImport(CoreVideo)] public static extern nint CVPixelBufferGetBaseAddress(nint pb);
    [DllImport(CoreVideo)] public static extern nuint CVPixelBufferGetBytesPerRow(nint pb);
    [DllImport(CoreVideo)] public static extern nuint CVPixelBufferGetWidth(nint pb);
    [DllImport(CoreVideo)] public static extern nuint CVPixelBufferGetHeight(nint pb);

    /// <summary>Copies a BGRA pixel buffer into a Frame.</summary>
    public static Frame ToFrameFromPixelBuffer(nint pb)
    {
        const ulong ReadOnly = 1;
        if (CVPixelBufferLockBaseAddress(pb, ReadOnly) != 0) throw new InvalidOperationException("Couldn't read the captured picture.");
        try
        {
            int w = (int)CVPixelBufferGetWidth(pb), h = (int)CVPixelBufferGetHeight(pb), stride = (int)CVPixelBufferGetBytesPerRow(pb);
            var span = new ReadOnlySpan<byte>((void*)CVPixelBufferGetBaseAddress(pb), stride * h);
            var f = Frame.FromBgra(w, h, span, stride);
            OpaqueAlpha(f.Px);
            return f;
        }
        finally { CVPixelBufferUnlockBaseAddress(pb, ReadOnly); }
    }

    /// <summary>
    /// The part of a captured window below <paramref name="top"/>, shrunk by <paramref name="scale"/>: one pixel from
    /// each scale x scale block. On Retina macOS shows each game pixel as such a block, so this is the game's own
    /// picture. It takes the block's bottom-right pixel: the dark line macOS draws along the top of a window's
    /// content lies over the top row of the first blocks.
    /// </summary>
    public static Frame ToGameFrameFromPixelBuffer(nint pb, int top, int scale)
    {
        const ulong ReadOnly = 1;
        if (CVPixelBufferLockBaseAddress(pb, ReadOnly) != 0) throw new InvalidOperationException("Couldn't read the captured picture.");
        try
        {
            int w = (int)CVPixelBufferGetWidth(pb), h = (int)CVPixelBufferGetHeight(pb), stride = (int)CVPixelBufferGetBytesPerRow(pb);
            int gw = w / scale, gh = (h - top) / scale;
            if (gw < 1 || gh < 1) throw new InvalidOperationException("The window's picture is too small.");
            var f = new Frame(gw, gh);
            var px = f.Px;
            byte* baseAddr = (byte*)CVPixelBufferGetBaseAddress(pb);
            int off = scale - 1;
            const int Opaque = unchecked((int)0xff000000);
            for (int y = 0; y < gh; y++)
            {
                int* row = (int*)(baseAddr + (long)(top + y * scale + off) * stride);
                int o = y * gw;
                for (int x = 0; x < gw; x++) px[o + x] = row[x * scale + off] | Opaque;
            }
            return f;
        }
        finally { CVPixelBufferUnlockBaseAddress(pb, ReadOnly); }
    }

    // ---- libdispatch ----
    [DllImport(ObjC.LibSystem)] public static extern nint dispatch_queue_create(string label, nint attr);
    [DllImport(ObjC.LibSystem)] public static extern void dispatch_release(nint obj);
}

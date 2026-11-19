using MapleLite.Core.Imaging;
using SkiaSharp;

namespace MapleLite.Sdk;

/// <summary>
/// PNG in and out for frames, with SkiaSharp (it ships with Avalonia on every OS). Works without starting the UI,
/// so plugins can load pictures on background threads and tests can load saved captures.
/// </summary>
public static class FrameFiles
{
    /// <summary>Reads a picture. With <paramref name="opaque"/> (captures) alpha is forced to 255; otherwise it's kept, unpremultiplied.</summary>
    public static Frame LoadPng(string path, bool opaque = true)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException($"{path} isn't a picture SkiaSharp can read.");
        return Decode(codec, opaque, path);
    }

    public static Frame DecodePng(byte[] bytes, bool opaque = false)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Those bytes aren't a picture SkiaSharp can read.");
        return Decode(codec, opaque, "picture");
    }

    static Frame Decode(SKCodec codec, bool opaque, string what)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        var result = codec.GetPixels(info, bmp.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) throw new InvalidDataException($"Couldn't decode {what} ({result}).");
        var frame = Frame.FromBgra(bmp.Width, bmp.Height, bmp.GetPixelSpan(), bmp.RowBytes);
        if (opaque) for (int i = 0; i < frame.Px.Length; i++) frame.Px[i] |= Pixel.Black;
        return frame;
    }

    /// <summary>
    /// Encodes a frame as PNG. Alpha is kept unless <paramref name="opaque"/>.
    /// <paramref name="fast"/> trades about 20% larger files for about 6× quicker encoding (a 1680x944 game picture:
    /// ~60 ms instead of ~370 ms), which recording needs to keep up. Both are lossless.
    /// </summary>
    public static byte[] EncodePng(Frame frame, bool opaque = true, bool fast = false)
    {
        var info = new SKImageInfo(frame.W, frame.H, SKColorType.Bgra8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(frame.Px, 0, bmp.GetPixels(), frame.Px.Length);
        if (fast)
        {
            using var pixels = bmp.PeekPixels();
            using var quick = pixels.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1));
            return quick.ToArray();
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static void SavePng(Frame frame, string path, bool opaque = true) => File.WriteAllBytes(path, EncodePng(frame, opaque));

    /// <summary>
    /// Encodes a frame as JPEG. <paramref name="fullColor"/> keeps color at full resolution (4:4:4), which keeps
    /// small colored game text sharp; the usual 4:2:0 halves color detail for slightly smaller files.
    /// At quality 92–95 with full color, game pictures look the same as the original at a fraction of PNG's size.
    /// </summary>
    public static byte[] EncodeJpeg(Frame frame, int quality = 92, bool fullColor = true)
    {
        using var bmp = ToSkia(frame);
        using var pixels = bmp.PeekPixels();
        using var data = pixels.Encode(new SKJpegEncoderOptions(Math.Clamp(quality, 1, 100),
            fullColor ? SKJpegEncoderDownsample.Downsample444 : SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
        return data.ToArray();
    }

    /// <summary>Decodes a PNG, JPEG or other picture, scaled down to at most <paramref name="maxWidth"/> wide (kept as is when null or larger).</summary>
    public static Frame Decode(byte[] bytes, int? maxWidth = null)
    {
        using var bmp = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Those bytes aren't a picture SkiaSharp can read.");
        if (maxWidth is not { } mw || bmp.Width <= mw) return FromSkia(bmp);
        using var small = Resize(bmp, mw, Math.Max(1, (int)Math.Round(bmp.Height * (double)mw / bmp.Width)));
        return FromSkia(small);
    }

    /// <summary>A smooth resize (for thumbnails and smaller exports).</summary>
    public static Frame Resize(Frame frame, int w, int h)
    {
        if (w == frame.W && h == frame.H) return frame;
        using var bmp = ToSkia(frame);
        using var small = Resize(bmp, w, h);
        return FromSkia(small);
    }

    static SKBitmap Resize(SKBitmap bmp, int w, int h)
    {
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        using var img = SKImage.FromBitmap(bmp);
        canvas.DrawImage(img, new SKRect(0, 0, w, h), paint);
        return dst;
    }

    /// <summary>A SkiaSharp copy of the frame (opaque BGRA), for drawing with Skia.</summary>
    public static SKBitmap ToSkia(Frame frame)
    {
        // Opaque: Skia ignores the alpha bytes, so a picture with stray alpha still draws solid.
        var bmp = new SKBitmap(new SKImageInfo(frame.W, frame.H, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(frame.Px, 0, bmp.GetPixels(), frame.Px.Length); // RowBytes is W*4 for a new bitmap
        return bmp;
    }

    /// <summary>The bitmap's pixels as a frame (alpha forced opaque).</summary>
    public static Frame FromSkia(SKBitmap bmp)
    {
        if (bmp.ColorType != SKColorType.Bgra8888)
        {
            using var conv = new SKBitmap(new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            bmp.CopyTo(conv, SKColorType.Bgra8888);
            return FromSkia(conv);
        }
        var f = Frame.FromBgra(bmp.Width, bmp.Height, bmp.GetPixelSpan(), bmp.RowBytes);
        for (int i = 0; i < f.Px.Length; i++) f.Px[i] |= Pixel.Black;
        return f;
    }
}

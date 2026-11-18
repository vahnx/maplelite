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
}

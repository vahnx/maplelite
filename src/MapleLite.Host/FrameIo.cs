using MapleLite.Core.Imaging;
using SkiaSharp;

namespace MapleLite.Host;

/// <summary>PNG in and out with SkiaSharp (ships with Avalonia on every OS), usable without starting the UI.</summary>
internal static class FrameIo
{
    public static Frame LoadPng(string path)
    {
        using var decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException($"{path} isn't a picture SkiaSharp can read.");
        using var bgra = decoded.ColorType == SKColorType.Bgra8888 ? decoded.Copy() : decoded.Copy(SKColorType.Bgra8888);
        var frame = Frame.FromBgra(bgra.Width, bgra.Height, bgra.GetPixelSpan(), bgra.RowBytes);
        for (int i = 0; i < frame.Px.Length; i++) frame.Px[i] |= Pixel.Black; // captures are opaque
        return frame;
    }

    public static void SavePng(Frame frame, string path)
    {
        var info = new SKImageInfo(frame.W, frame.H, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(frame.Px, 0, bmp.GetPixels(), frame.Px.Length);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}

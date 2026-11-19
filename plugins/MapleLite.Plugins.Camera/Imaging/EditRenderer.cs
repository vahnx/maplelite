using System.Globalization;
using MapleLite.Core.Imaging;
using MapleLite.Plugins.Camera.Model;
using MapleLite.Sdk;
using SkiaSharp;

namespace MapleLite.Plugins.Camera.Imaging;

/// <summary>
/// What a picture is drawn from: the original, when it was taken, and what the overlay showed then.
/// <paramref name="PictureScale"/> is the picture's pixels per original pixel: previews pass a smaller copy (e.g. 0.5)
/// so edits, which are in original pixels, land in the same places as in the full-size export.
/// </summary>
public sealed record RenderSource(Frame Picture, DateTimeOffset Taken, IReadOnlyList<OverlayItem>? Overlays = null, double OverlayScale = 1, double PictureScale = 1)
{
    public int W => Math.Max(1, (int)Math.Round(Picture.W / PictureScale));
    public int H => Math.Max(1, (int)Math.Round(Picture.H / PictureScale));
}

/// <summary>
/// Where things landed in a render, so a preview can map clicks back to the original picture:
/// original = (output − Pad) / Scale + Crop corner.
/// </summary>
public sealed record RenderLayout(double Scale, double PadLeft, double PadTop, CropBox Crop, int Width, int Height)
{
    public (double X, double Y) ToSource(double outX, double outY) => ((outX - PadLeft) / Scale + Crop.X, (outY - PadTop) / Scale + Crop.Y);
    public (double X, double Y) ToOutput(double srcX, double srcY) => ((srcX - Crop.X) * Scale + PadLeft, (srcY - Crop.Y) * Scale + PadTop);
}

/// <summary>The rendered picture, its layout, and where each text landed (in original pixels, for picking and moving text).</summary>
public sealed record RenderResult(Frame Picture, RenderLayout Layout, IReadOnlyList<SKRect> TextBounds);

/// <summary>
/// Draws an <see cref="Edit"/> over a picture with SkiaSharp: crop, look and adjustments, effects, overlay items,
/// drawing, text, then the frame and date stamp. Thread-safe and UI-free, so previews and video exports run off the
/// UI thread. The same code renders the preview (smaller scale) and the export (full size).
/// </summary>
public static class EditRenderer
{
    /// <param name="scale">Output pixels per original pixel (below 1 for previews).</param>
    /// <param name="ignoreCrop">Draw the whole picture (the crop tool shows everything while you pick).</param>
    public static RenderResult Render(RenderSource src, Edit edit, double scale = 1, bool ignoreCrop = false)
    {
        var pic = src.Picture;
        var crop = !ignoreCrop && edit.Crop is { } c ? Clamp(c, src.W, src.H) : new CropBox(0, 0, src.W, src.H);
        int cw = Math.Max(1, (int)Math.Round(crop.W * scale)), ch = Math.Max(1, (int)Math.Round(crop.H * scale));
        var frame = ignoreCrop ? FrameStyle.None : edit.Frame;
        var pad = Frames.Padding(frame, cw, ch);
        int w = cw + (int)Math.Round(pad.Left + pad.Right), h = ch + (int)Math.Round(pad.Top + pad.Bottom);
        var content = SKRect.Create((float)pad.Left, (float)pad.Top, cw, ch);

        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        Frames.DrawBack(canvas, frame, edit, w, h, content);

        canvas.Save();
        Frames.ClipContent(canvas, frame, content);
        canvas.Translate(content.Left, content.Top);
        canvas.Scale((float)scale);
        canvas.Translate(-crop.X, -crop.Y);

        using (var source = FrameFiles.ToSkia(pic))
            DrawPicture(canvas, source, edit, crop, scale * src.PictureScale, src.PictureScale);
        canvas.Restore();

        if (edit.Vignette > 0 || edit.Look is Look.Vintage or Look.Night)
        {
            double v = Math.Clamp(edit.Vignette + (edit.Look switch { Look.Vintage => 0.35, Look.Night => 0.45, _ => 0 }) * edit.Strength, 0, 1);
            DrawVignette(canvas, content, v, frame);
        }

        canvas.Save();
        Frames.ClipContent(canvas, frame, content);
        canvas.Translate(content.Left, content.Top);
        canvas.Scale((float)scale);
        canvas.Translate(-crop.X, -crop.Y);
        if (edit.Overlays && src.Overlays is { Count: > 0 } items) OverlayPainter.Draw(canvas, items, src.OverlayScale);
        foreach (var s in edit.Strokes) DrawStroke(canvas, s);
        var bounds = new List<SKRect>();
        foreach (var t in edit.Texts) bounds.Add(DrawText(canvas, t));
        canvas.Restore();

        Frames.DrawFront(canvas, frame, edit, w, h, content);
        if (edit.Stamp) DrawStamp(canvas, content, src.Taken);
        canvas.Flush();

        var outFrame = FrameFiles.FromSkia(bmp);
        return new RenderResult(outFrame, new RenderLayout(scale, pad.Left, pad.Top, crop, w, h), bounds);
    }

    /// <summary>The output size for an edit, without drawing (for choosing export sizes).</summary>
    public static (int W, int H) OutputSize(int picW, int picH, Edit edit, double scale = 1)
    {
        var crop = edit.Crop is { } c ? Clamp(c, picW, picH) : new CropBox(0, 0, picW, picH);
        int cw = Math.Max(1, (int)Math.Round(crop.W * scale)), ch = Math.Max(1, (int)Math.Round(crop.H * scale));
        var pad = Frames.Padding(edit.Frame, cw, ch);
        return (cw + (int)Math.Round(pad.Left + pad.Right), ch + (int)Math.Round(pad.Top + pad.Bottom));
    }

    public static CropBox Clamp(CropBox c, int w, int h)
    {
        int x = Math.Clamp(c.X, 0, w - 1), y = Math.Clamp(c.Y, 0, h - 1);
        return new CropBox(x, y, Math.Clamp(c.W, 1, w - x), Math.Clamp(c.H, 1, h - y));
    }

    /// <param name="pixelScale">Output pixels per picture pixel (for choosing smooth or crisp resampling).</param>
    /// <param name="pictureScale">Picture pixels per original pixel.</param>
    static void DrawPicture(SKCanvas canvas, SKBitmap source, Edit edit, CropBox crop, double pixelScale, double pictureScale)
    {
        var matrix = ColorMatrix.For(edit);
        using var filter = matrix.IsIdentity ? null : SKColorFilter.CreateColorMatrix(matrix.M);
        // Game art is pixel art: when shrinking, smooth it; at full size or larger keep pixels crisp.
        using var paint = new SKPaint { ColorFilter = filter, FilterQuality = Math.Abs(pixelScale - 1) > 0.001 && pixelScale < 1 ? SKFilterQuality.Medium : SKFilterQuality.None };
        using var image = SKImage.FromBitmap(source);
        var area = SKRect.Create(crop.X, crop.Y, crop.W, crop.H); // where it goes (original pixels)
        float ps = (float)pictureScale;
        var from = SKRect.Create(crop.X * ps, crop.Y * ps, crop.W * ps, crop.H * ps); // where it comes from (picture pixels)

        if (edit.Look == Look.Retro && edit.Strength > 0)
        {
            // Big chunky pixels and fewer color steps, like an old console.
            // block size in original pixels, so the preview's blocks match the export's
            int block = Math.Max(2, (int)Math.Round(Math.Min(crop.W, crop.H) / 180.0 * Math.Clamp(edit.Strength, 0.3, 1)));
            int sw = Math.Max(1, crop.W / block), sh = Math.Max(1, crop.H / block);
            using var small = new SKBitmap(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var sc = new SKCanvas(small))
            using (var down = new SKPaint { FilterQuality = SKFilterQuality.Medium })
                sc.DrawImage(image, from, new SKRect(0, 0, sw, sh), down);
            using var poster = SKColorFilter.CreateTable(Posterize(6));
            using var both = filter == null ? poster : SKColorFilter.CreateCompose(poster, filter);
            using var up = new SKPaint { ColorFilter = both, FilterQuality = SKFilterQuality.None };
            using var smallImage = SKImage.FromBitmap(small);
            canvas.DrawImage(smallImage, new SKRect(0, 0, sw, sh), area, up);
            return;
        }

        canvas.DrawImage(image, from, area, paint);

        if (edit.Look == Look.Dreamy && edit.Strength > 0)
        {
            // A soft glow: a blurred copy laid over with Screen.
            float radius = (float)(Math.Min(crop.W, crop.H) / 90.0);
            using var blur = SKImageFilter.CreateBlur(radius, radius);
            using var glow = new SKPaint { ImageFilter = blur, ColorFilter = filter, BlendMode = SKBlendMode.Screen, Color = SKColors.White.WithAlpha((byte)(90 * edit.Strength)) };
            canvas.DrawImage(image, from, area, glow);
        }
    }

    static byte[] Posterize(int levels)
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = (byte)Math.Round(Math.Round(i / 255.0 * (levels - 1)) / (levels - 1) * 255);
        return t;
    }

    static void DrawVignette(SKCanvas canvas, SKRect r, double amount, FrameStyle frame)
    {
        float radius = (float)(Math.Sqrt(r.Width * r.Width + r.Height * r.Height) / 2);
        using var shader = SKShader.CreateRadialGradient(new SKPoint(r.MidX, r.MidY), radius,
            [SKColors.Transparent, SKColors.Transparent, SKColors.Black.WithAlpha((byte)(220 * amount))], [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.Save();
        Frames.ClipContent(canvas, frame, r);
        canvas.DrawRect(r, paint);
        canvas.Restore();
    }

    static void DrawStroke(SKCanvas canvas, Stroke s)
    {
        if (s.Points.Count < 2) return;
        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
            StrokeWidth = (float)s.Width, Color = ParseColor(s.Color),
        };
        if (s.Points.Count < 4) { paint.Style = SKPaintStyle.Fill; canvas.DrawCircle((float)s.Points[0], (float)s.Points[1], (float)s.Width / 2, paint); return; }
        using var path = new SKPath();
        path.MoveTo((float)s.Points[0], (float)s.Points[1]);
        for (int i = 2; i + 1 < s.Points.Count; i += 2) path.LineTo((float)s.Points[i], (float)s.Points[i + 1]);
        canvas.DrawPath(path, paint);
    }

    static SKTypeface? _bold, _black;
    static SKTypeface Bold => _bold ??= SKTypeface.FromFamilyName(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;
    /// <summary>A heavy face for the outlined "meme" style, when the system has one.</summary>
    static SKTypeface Heavy => _black ??= SKTypeface.FromFamilyName("Arial Black") is { } t && t.FamilyName.Contains("Black", StringComparison.OrdinalIgnoreCase) ? t
        : SKTypeface.FromFamilyName(null, SKFontStyleWeight.Black, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? Bold;

    /// <summary>Draws one text mark centred on its point; returns its bounds.</summary>
    internal static SKRect DrawText(SKCanvas canvas, TextMark t)
    {
        var lines = (t.Text.Length == 0 ? " " : t.Text).Replace("\r", "").Split('\n');
        float size = (float)Math.Max(4, t.Size);
        using var paint = new SKPaint { IsAntialias = true, TextSize = size, Typeface = t.Style == TextStyle.Outline ? Heavy : Bold, Color = ParseColor(t.Color) };
        var m = paint.FontMetrics;
        float lineH = (m.Descent - m.Ascent) * 1.08f;
        float width = lines.Max(l => paint.MeasureText(l));
        float height = lineH * lines.Length;
        float x0 = (float)t.X - width / 2, y0 = (float)t.Y - height / 2;
        float pad = size * 0.35f;
        var box = new SKRect(x0 - pad, y0 - pad * 0.6f, x0 + width + pad, y0 + height + pad * 0.6f);

        switch (t.Style)
        {
            case TextStyle.Pill:
                using (var back = new SKPaint { IsAntialias = true, Color = new SKColor(10, 12, 18, 185) })
                    canvas.DrawRoundRect(box, size * 0.3f, size * 0.3f, back);
                break;
            case TextStyle.Bubble:
                using (var back = new SKPaint { IsAntialias = true, Color = SKColors.White })
                using (var edge = new SKPaint { IsAntialias = true, Color = new SKColor(20, 20, 24), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, size * 0.07f) })
                using (var path = new SKPath())
                {
                    float r = size * 0.45f;
                    path.AddRoundRect(box, r, r);
                    var tail = new SKPath();
                    tail.MoveTo(box.Left + box.Width * 0.22f, box.Bottom - 1);
                    tail.LineTo(box.Left + box.Width * 0.12f, box.Bottom + size * 0.7f);
                    tail.LineTo(box.Left + box.Width * 0.38f, box.Bottom - 1);
                    tail.Close();
                    using var shape = path.Op(tail, SKPathOp.Union) ?? path;
                    canvas.DrawPath(shape, back);
                    canvas.DrawPath(shape, edge);
                    tail.Dispose();
                    box = new SKRect(box.Left, box.Top, box.Right, box.Bottom + size * 0.7f);
                }
                if (t.Color.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase)) paint.Color = new SKColor(20, 20, 24); // white on white can't be read
                break;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            float lw = paint.MeasureText(lines[i]);
            float x = (float)t.X - lw / 2, y = y0 + i * lineH - m.Ascent;
            switch (t.Style)
            {
                case TextStyle.Outline:
                    using (var stroke = new SKPaint { IsAntialias = true, TextSize = size, Typeface = paint.Typeface, Style = SKPaintStyle.Stroke, StrokeWidth = size * 0.14f, StrokeJoin = SKStrokeJoin.Round, Color = SKColors.Black })
                        canvas.DrawText(lines[i], x, y, stroke);
                    break;
                case TextStyle.Shadow:
                    using (var shadow = new SKPaint { IsAntialias = true, TextSize = size, Typeface = paint.Typeface, Color = SKColors.Black.WithAlpha(170), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.06f) })
                        canvas.DrawText(lines[i], x + size * 0.06f, y + size * 0.08f, shadow);
                    break;
            }
            canvas.DrawText(lines[i], x, y, paint);
        }
        return box;
    }

    static void DrawStamp(SKCanvas canvas, SKRect content, DateTimeOffset taken)
    {
        string text = taken.ToString("yy MM dd  HH:mm", CultureInfo.InvariantCulture).Insert(0, "'");
        float size = Math.Max(10, content.Height * 0.045f);
        using var paint = new SKPaint { IsAntialias = true, TextSize = size, Typeface = Bold, Color = new SKColor(255, 150, 40), TextAlign = SKTextAlign.Right };
        using var glow = new SKPaint { IsAntialias = true, TextSize = size, Typeface = Bold, Color = new SKColor(255, 90, 20, 160), TextAlign = SKTextAlign.Right, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.12f) };
        float x = content.Right - size * 0.8f, y = content.Bottom - size * 0.7f;
        canvas.DrawText(text, x, y, glow);
        canvas.DrawText(text, x, y, paint);
    }

    public static SKColor ParseColor(string? hex, SKColor? fallback = null) =>
        hex != null && SKColor.TryParse(hex, out var c) ? c : fallback ?? SKColors.White;
}

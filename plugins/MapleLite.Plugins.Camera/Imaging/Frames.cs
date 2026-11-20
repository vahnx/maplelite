using MapleLite.Plugins.Camera.Model;
using SkiaSharp;

namespace MapleLite.Plugins.Camera.Imaging;

/// <summary>The frames (borders) drawn around a picture. Sizes follow the picture, so previews and exports match.</summary>
internal static class Frames
{
    public readonly record struct Pad(double Left, double Top, double Right, double Bottom);

    public static Pad Padding(FrameStyle style, int w, int h)
    {
        double u = Math.Min(w, h);
        return style switch
        {
            FrameStyle.Simple => Even(u * 0.035),
            FrameStyle.Polaroid => new(u * 0.05, u * 0.05, u * 0.05, u * 0.2),
            FrameStyle.Film => new(u * 0.025, u * 0.1, u * 0.025, u * 0.1),
            FrameStyle.Maple => Even(u * 0.07),
            FrameStyle.Postcard => new(u * 0.05, u * 0.05, u * 0.05, u * 0.15),
            FrameStyle.Neon => Even(u * 0.06),
            FrameStyle.Rounded => Even(u * 0.045),
            _ => default,
        };
        static Pad Even(double p) => new(p, p, p, p);
    }

    /// <summary>Default color offered for each frame.</summary>
    public static string DefaultColor(FrameStyle style) => style switch
    {
        FrameStyle.Neon => "#FF3DCB",
        FrameStyle.Rounded => "#22252E",
        _ => "#FFFFFF",
    };

    public static bool TakesColor(FrameStyle s) => s is FrameStyle.Simple or FrameStyle.Polaroid or FrameStyle.Neon or FrameStyle.Rounded;
    public static bool TakesCaption(FrameStyle s) => s is FrameStyle.Polaroid or FrameStyle.Postcard;

    public static void ClipContent(SKCanvas canvas, FrameStyle style, SKRect content)
    {
        if (style == FrameStyle.Rounded) canvas.ClipRoundRect(new SKRoundRect(content, Radius(content)), antialias: true);
        else canvas.ClipRect(content);
    }

    static float Radius(SKRect r) => Math.Min(r.Width, r.Height) * 0.04f;

    public static void DrawBack(SKCanvas canvas, FrameStyle style, Edit e, int w, int h, SKRect content)
    {
        var all = new SKRect(0, 0, w, h);
        var color = EditRenderer.ParseColor(e.FrameColor, SKColors.White);
        switch (style)
        {
            case FrameStyle.Simple:
            case FrameStyle.Polaroid:
                using (var p = new SKPaint { Color = color }) canvas.DrawRect(all, p);
                break;
            case FrameStyle.Film:
                using (var p = new SKPaint { Color = new SKColor(18, 16, 15) }) canvas.DrawRect(all, p);
                break;
            case FrameStyle.Maple:
                using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h),
                           [new SKColor(255, 176, 59), new SKColor(232, 93, 36), new SKColor(176, 38, 32)], SKShaderTileMode.Clamp))
                using (var p = new SKPaint { Shader = shader }) canvas.DrawRect(all, p);
                break;
            case FrameStyle.Postcard:
                using (var p = new SKPaint { Color = new SKColor(246, 238, 220) }) canvas.DrawRect(all, p);
                AirmailEdge(canvas, all, Math.Min(content.Left, content.Top) * 0.35f);
                break;
            case FrameStyle.Neon:
                using (var p = new SKPaint { Color = new SKColor(11, 10, 22) }) canvas.DrawRect(all, p);
                break;
            case FrameStyle.Rounded:
                using (var p = new SKPaint { Color = color }) canvas.DrawRect(all, p);
                using (var shadow = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(150), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, content.Left * 0.35f) })
                    canvas.DrawRoundRect(new SKRoundRect(SKRect.Create(content.Left, content.Top + content.Left * 0.2f, content.Width, content.Height), Radius(content)), shadow);
                break;
        }
    }

    public static void DrawFront(SKCanvas canvas, FrameStyle style, Edit e, int w, int h, SKRect content)
    {
        switch (style)
        {
            case FrameStyle.Polaroid:
                Caption(canvas, e.Caption, new SKRect(content.Left, content.Bottom, content.Right, h), new SKColor(40, 40, 46), italic: true);
                break;
            case FrameStyle.Film:
                FilmHoles(canvas, w, content);
                break;
            case FrameStyle.Maple:
            {
                using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, content.Left * 0.06f), Color = new SKColor(255, 240, 210, 220) };
                var inner = content; inner.Inflate(content.Left * 0.25f, content.Left * 0.25f);
                canvas.DrawRect(inner, line);
                float size = content.Left * 1.7f;
                Leaf(canvas, content.Left * 0.55f, content.Top * 0.55f, size, -25, new SKColor(255, 226, 120, 235));
                Leaf(canvas, w - content.Left * 0.55f, content.Top * 0.55f, size * 0.8f, 30, new SKColor(255, 240, 200, 220));
                Leaf(canvas, content.Left * 0.55f, h - content.Top * 0.55f, size * 0.8f, 200, new SKColor(255, 240, 200, 220));
                Leaf(canvas, w - content.Left * 0.55f, h - content.Top * 0.55f, size, 160, new SKColor(255, 226, 120, 235));
                break;
            }
            case FrameStyle.Postcard:
            {
                var text = e.Caption.Trim().Length > 0 ? "Greetings from " + e.Caption.Trim() : "Greetings from Maple World";
                Caption(canvas, text, new SKRect(content.Left, content.Bottom, content.Right, h), new SKColor(176, 38, 32), italic: false);
                break;
            }
            case FrameStyle.Neon:
            {
                var color = EditRenderer.ParseColor(e.FrameColor, SKColors.Magenta);
                float sw = Math.Max(2, content.Left * 0.12f);
                var r = content; r.Inflate(sw * 1.6f, sw * 1.6f);
                using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = sw * 2.4f, Color = color.WithAlpha(150), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sw * 2) };
                using var core = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = sw, Color = Lighten(color) };
                canvas.DrawRoundRect(r, sw * 2, sw * 2, glow);
                canvas.DrawRoundRect(r, sw * 2, sw * 2, core);
                break;
            }
        }
    }

    static SKColor Lighten(SKColor c) => new((byte)((c.Red + 255) / 2), (byte)((c.Green + 255) / 2), (byte)((c.Blue + 255) / 2));

    static void Caption(SKCanvas canvas, string text, SKRect area, SKColor color, bool italic)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var face = SKTypeface.FromFamilyName(italic ? "Segoe Print" : "Georgia",
            italic ? SKFontStyleWeight.Normal : SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        using var paint = new SKPaint { IsAntialias = true, Color = color, Typeface = face, TextAlign = SKTextAlign.Center, TextSize = area.Height * 0.36f };
        float max = area.Width * 0.92f, wText = paint.MeasureText(text);
        if (wText > max) paint.TextSize *= max / wText;
        var m = paint.FontMetrics;
        canvas.DrawText(text, area.MidX, area.MidY - (m.Ascent + m.Descent) / 2, paint);
    }

    static void FilmHoles(SKCanvas canvas, int w, SKRect content)
    {
        float band = content.Top, hole = band * 0.42f, gap = hole * 0.9f;
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(235, 230, 220) };
        for (float x = gap; x + hole < w - gap * 0.5f; x += hole + gap)
        {
            canvas.DrawRoundRect(SKRect.Create(x, (band - hole) / 2, hole, hole), hole * 0.2f, hole * 0.2f, p);
            canvas.DrawRoundRect(SKRect.Create(x, content.Bottom + (band - hole) / 2, hole, hole), hole * 0.2f, hole * 0.2f, p);
        }
    }

    static void AirmailEdge(SKCanvas canvas, SKRect all, float band)
    {
        if (band < 1) return;
        using var red = new SKPaint { IsAntialias = true, Color = new SKColor(196, 48, 43) };
        using var blue = new SKPaint { IsAntialias = true, Color = new SKColor(36, 72, 150) };
        canvas.Save();
        var inner = all; inner.Inflate(-band, -band);
        canvas.ClipRect(inner, SKClipOperation.Difference);
        float step = band * 2.2f; int i = 0;
        for (float x = -all.Height; x < all.Width + all.Height; x += step, i++)
        {
            using var path = new SKPath();
            path.MoveTo(x, 0); path.LineTo(x + step / 2, 0); path.LineTo(x + step / 2 + all.Height, all.Height); path.LineTo(x + all.Height, all.Height); path.Close();
            canvas.DrawPath(path, i % 2 == 0 ? red : blue);
        }
        canvas.Restore();
    }

    /// <summary>A simple five-pointed leaf shape (our own drawing), centred on (x, y), <paramref name="size"/> tall.</summary>
    internal static void Leaf(SKCanvas canvas, float x, float y, float size, float degrees, SKColor color)
    {
        // Half the outline (tip at the top, stem at the bottom), mirrored for the other half. Units: −1..1.
        (float X, float Y)[] half =
        [
            (0, -1f), (0.13f, -0.72f), (0.3f, -0.8f), (0.25f, -0.42f), (0.62f, -0.62f), (0.56f, -0.42f),
            (0.95f, -0.42f), (0.78f, -0.22f), (0.88f, -0.1f), (0.46f, 0.14f), (0.52f, 0.3f), (0.07f, 0.22f), (0.05f, 0.5f),
        ];
        using var path = new SKPath();
        path.MoveTo(half[0].X, half[0].Y);
        foreach (var p in half.Skip(1)) path.LineTo(p.X, p.Y);
        foreach (var p in Enumerable.Reverse(half).Skip(1)) path.LineTo(-p.X, p.Y);
        path.Close();
        using var stem = new SKPath();
        stem.MoveTo(0, 0.45f); stem.LineTo(0, 1f);
        var m = SKMatrix.CreateScale(size / 2, size / 2).PostConcat(SKMatrix.CreateRotationDegrees(degrees)).PostConcat(SKMatrix.CreateTranslation(x, y));
        path.Transform(m); stem.Transform(m);
        using var fill = new SKPaint { IsAntialias = true, Color = color };
        using var line = new SKPaint { IsAntialias = true, Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, size * 0.05f), StrokeCap = SKStrokeCap.Round };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(stem, line);
    }
}

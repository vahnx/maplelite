using SkiaSharp;

namespace MapleLite.Sdk;

/// <summary>
/// Draws <see cref="OverlayItem"/>s into a picture the way the live overlay shows them over the game (dark pill
/// labels, circular timers), with SkiaSharp. Works on any thread, without the UI.
/// </summary>
public static class OverlayPainter
{
    /// <summary>
    /// Draws <paramref name="items"/> on <paramref name="canvas"/>, whose units are game-picture pixels.
    /// <paramref name="scale"/> is the game window's display scale (1.5 at 150%): the live overlay sizes its labels
    /// in screen points, so this keeps them the same size relative to the game.
    /// </summary>
    public static void Draw(SKCanvas canvas, IEnumerable<OverlayItem> items, double scale = 1)
    {
        float s = (float)Math.Max(0.25, scale);
        using var back = new SKPaint { IsAntialias = true, Color = new SKColor(10, 12, 18, 150) };
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 * s, Color = new SKColor(255, 255, 255, 90) };
        using var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f * s, StrokeCap = SKStrokeCap.Butt };
        using var pill = new SKPaint { IsAntialias = true, Color = new SKColor(10, 12, 18, 170) };
        using var text = new SKPaint { IsAntialias = true };
        using var labelOutline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * s };
        using var typeface = SKTypeface.FromFamilyName(null, SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;
        using var rectangle = new SKPaint { IsAntialias = true, StrokeWidth = 2 * s };
        foreach (var it in items.OrderBy(it => !it.Dim))
        {
            float x = (float)it.X, y = (float)it.Y;
            var color = it.ColorRgb is { } custom ? new SKColor(custom | 0xFF000000u) : ToneColor(it.Tone);
            if (it.Rectangle is { } box)
            {
                rectangle.Style = it.Dim ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
                rectangle.Color = it.Dim ? new SKColor(0, 0, 0, 170) : color;
                var inset = it.Dim ? 0 : s;
                canvas.DrawRect(new SKRect(box.X + inset, box.Y + inset, box.Right - inset, box.Bottom - inset), rectangle);
            }
            if (it.Ring is { } ring)
            {
                float r = 13 * s;
                canvas.DrawCircle(x, y, r, back);
                canvas.DrawCircle(x, y, r, rim);
                if (ring > 0.001)
                {
                    arc.Color = color;
                    float rr = r - 1.5f * s;
                    using var path = new SKPath();
                    path.AddArc(new SKRect(x - rr, y - rr, x + rr, y + rr), -90, (float)(Math.Min(ring, 0.9999) * 360));
                    canvas.DrawPath(path, arc);
                }
            }
            if (it.Text.Length == 0) continue;
            text.Typeface = typeface;
            text.TextSize = (float)it.Size * s;
            float w = text.MeasureText(it.Text);
            var m = text.FontMetrics;
            float th = m.Descent - m.Ascent, padX = 5 * s, padY = 1 * s;
            float bw = w + 2 * padX, bh = th + 2 * padY;
            float left = x - bw / 2, top = it.Ring != null ? y + 15 * s : y - bh / 2;
            var bounds = new SKRect(left, top, left + bw, top + bh);
            canvas.DrawRoundRect(bounds, 4 * s, 4 * s, pill);
            if (it.LabelAccentRgb is { } rgb)
            {
                labelOutline.Color = new SKColor(rgb | 0xFF000000u);
                canvas.DrawRoundRect(bounds, 4 * s, 4 * s, labelOutline);
            }
            text.Color = color;
            canvas.DrawText(it.Text, left + padX, top + padY - m.Ascent, text);
        }
    }

    /// <summary>The color each tone has on the overlay.</summary>
    public static SKColor ToneColor(OverlayTone t) => t switch
    {
        OverlayTone.Accent => new SKColor(255, 170, 60),
        OverlayTone.Good => new SKColor(110, 220, 120),
        OverlayTone.Warning => new SKColor(255, 215, 80),
        OverlayTone.Danger => new SKColor(255, 100, 90),
        OverlayTone.Muted => new SKColor(180, 185, 195),
        _ => SKColors.White,
    };
}

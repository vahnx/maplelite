using MapleLite.Plugins.Camera.Model;

namespace MapleLite.Plugins.Camera.Imaging;

/// <summary>
/// A 4x5 color matrix in Skia's layout (rows R, G, B, A; columns r, g, b, a, offset), offsets in 0–1.
/// Looks and adjustments are matrices so they combine into one pass over the picture.
/// </summary>
internal readonly struct ColorMatrix
{
    public readonly float[] M;
    public ColorMatrix(float[] m) => M = m;

    public static ColorMatrix Identity => new([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0]);

    const float Lr = 0.2126f, Lg = 0.7152f, Lb = 0.0722f;

    /// <summary>This, then <paramref name="next"/>.</summary>
    public ColorMatrix Then(ColorMatrix next)
    {
        var a = next.M; var b = M; var r = new float[20];
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                float s = 0;
                for (int k = 0; k < 4; k++) s += a[row * 5 + k] * b[k * 5 + col];
                r[row * 5 + col] = s;
            }
            float o = a[row * 5 + 4];
            for (int k = 0; k < 4; k++) o += a[row * 5 + k] * b[k * 5 + 4];
            r[row * 5 + 4] = o;
        }
        return new ColorMatrix(r);
    }

    /// <summary>Part of the way from unchanged (0) to this (1).</summary>
    public ColorMatrix Mix(double amount)
    {
        var id = Identity.M; var r = new float[20];
        float t = (float)Math.Clamp(amount, 0, 1);
        for (int i = 0; i < 20; i++) r[i] = id[i] + (M[i] - id[i]) * t;
        return new ColorMatrix(r);
    }

    public bool IsIdentity
    {
        get { var id = Identity.M; for (int i = 0; i < 20; i++) if (Math.Abs(M[i] - id[i]) > 1e-5) return false; return true; }
    }

    public static ColorMatrix Scale(float r, float g, float b, float offR = 0, float offG = 0, float offB = 0) =>
        new([r, 0, 0, 0, offR, 0, g, 0, 0, offG, 0, 0, b, 0, offB, 0, 0, 0, 1, 0]);

    public static ColorMatrix Saturation(float s)
    {
        float ir = (1 - s) * Lr, ig = (1 - s) * Lg, ib = (1 - s) * Lb;
        return new([ir + s, ig, ib, 0, 0, ir, ig + s, ib, 0, 0, ir, ig, ib + s, 0, 0, 0, 0, 0, 1, 0]);
    }

    /// <summary>Contrast around mid grey (1 = unchanged).</summary>
    public static ColorMatrix Contrast(float k) { float o = 0.5f * (1 - k); return Scale(k, k, k, o, o, o); }

    public static ColorMatrix Brightness(float add) => Scale(1, 1, 1, add, add, add);

    /// <summary>Lifts the blacks and lowers the whites (a washed-out, printed look).</summary>
    public static ColorMatrix Fade(float amount) { float k = 1 - amount; return Scale(k, k, k, amount * 0.6f, amount * 0.6f, amount * 0.6f); }

    public static ColorMatrix Sepia => new([0.393f, 0.769f, 0.189f, 0, 0, 0.349f, 0.686f, 0.168f, 0, 0, 0.272f, 0.534f, 0.131f, 0, 0, 0, 0, 0, 1, 0]);
    public static ColorMatrix Invert => Scale(-1, -1, -1, 1, 1, 1);

    /// <summary>The color part of a look (Retro and Dreamy add effects beyond color, drawn by the renderer).</summary>
    public static ColorMatrix ForLook(Look look) => look switch
    {
        Look.BlackWhite => Saturation(0),
        Look.Noir => Saturation(0).Then(Contrast(1.45f)).Then(Brightness(-0.03f)),
        Look.Sepia => Sepia,
        Look.Vintage => Sepia.Mix(0.45).Then(Fade(0.12f)).Then(Scale(1.04f, 1, 0.92f)),
        Look.Warm => Scale(1.08f, 1.02f, 0.88f, 0.02f, 0.01f, 0),
        Look.Cool => Scale(0.9f, 1.0f, 1.08f, 0, 0.01f, 0.03f),
        Look.Vivid => Saturation(1.45f).Then(Contrast(1.12f)),
        Look.Dreamy => Saturation(0.85f).Then(Fade(0.1f)).Then(Scale(1.03f, 0.98f, 1.03f, 0.03f, 0.01f, 0.03f)),
        Look.Night => Saturation(0.35f).Then(Scale(0.55f, 0.7f, 1.05f, 0, 0.01f, 0.05f)).Then(Contrast(1.1f)),
        Look.Retro => Saturation(1.25f).Then(Contrast(1.08f)),
        Look.Invert => Invert,
        _ => Identity,
    };

    /// <summary>The look at its strength, then the adjustment sliders.</summary>
    public static ColorMatrix For(Edit e)
    {
        var m = ForLook(e.Look).Mix(e.Strength);
        if (e.Brightness != 0) m = m.Then(Brightness((float)(e.Brightness * 0.3)));
        if (e.Contrast != 0) m = m.Then(Contrast((float)(1 + e.Contrast * 0.6)));
        if (e.Saturation != 0) m = m.Then(Saturation((float)(1 + e.Saturation)));
        if (e.Warmth != 0) m = m.Then(Scale((float)(1 + e.Warmth * 0.12), 1, (float)(1 - e.Warmth * 0.12)));
        return m;
    }
}

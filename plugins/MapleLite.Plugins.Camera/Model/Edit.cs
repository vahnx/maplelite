using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapleLite.Plugins.Camera.Model;

/// <summary>A color look applied to the whole picture, like a phone camera filter.</summary>
public enum Look { Original, BlackWhite, Noir, Sepia, Vintage, Warm, Cool, Vivid, Dreamy, Night, Retro, Invert }

/// <summary>A frame (border) drawn around the picture.</summary>
public enum FrameStyle { None, Simple, Polaroid, Film, Maple, Postcard, Neon, Rounded }

/// <summary>How a piece of text is drawn.</summary>
public enum TextStyle { Outline, Pill, Shadow, Bubble }

/// <summary>Text placed on the picture. Position and size are in the original picture's pixels (centre of the text).</summary>
public sealed class TextMark
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Size { get; set; } = 48;
    public string Color { get; set; } = "#FFFFFF";
    public TextStyle Style { get; set; } = TextStyle.Outline;
}

/// <summary>A freehand line, as x,y pairs in the original picture's pixels.</summary>
public sealed class Stroke
{
    public string Color { get; set; } = "#FF4D6D";
    public double Width { get; set; } = 8;
    public List<double> Points { get; set; } = new();
}

/// <summary>The part of the picture kept, in the original picture's pixels.</summary>
public sealed record CropBox(int X, int Y, int W, int H);

/// <summary>
/// Everything done to a snapshot or clip. Kept beside the capture, so the original is never changed and edits can be
/// reopened; exports bake it in. Clips use the same edit for every frame.
/// </summary>
public sealed class Edit
{
    public Look Look { get; set; }
    /// <summary>How strongly the look applies, 0–1.</summary>
    public double Strength { get; set; } = 1;
    /// <summary>−1 to 1, 0 is unchanged.</summary>
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Saturation { get; set; }
    public double Warmth { get; set; }
    /// <summary>Darker corners, 0–1.</summary>
    public double Vignette { get; set; }

    public FrameStyle Frame { get; set; }
    /// <summary>Color for the frames that take one (Simple, Polaroid, Neon, Rounded).</summary>
    public string FrameColor { get; set; } = "#FFFFFF";
    /// <summary>Written on Polaroid and Postcard frames.</summary>
    public string Caption { get; set; } = "";

    /// <summary>A date stamp in the corner, like an old film camera.</summary>
    public bool Stamp { get; set; }
    /// <summary>Include what other plugins drew over the game when it was taken (respawn timers, …).</summary>
    public bool Overlays { get; set; }

    public CropBox? Crop { get; set; }
    public List<TextMark> Texts { get; set; } = new();
    public List<Stroke> Strokes { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public Edit Clone() => JsonSerializer.Deserialize<Edit>(JsonSerializer.Serialize(this, Json), Json)!;
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static Edit? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<Edit>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Nothing changes the picture: exporting gives back the original.</summary>
    [JsonIgnore]
    public bool IsPlain => (Look == Look.Original || Strength <= 0) && Brightness == 0 && Contrast == 0 && Saturation == 0 && Warmth == 0
        && Vignette == 0 && Frame == FrameStyle.None && !Stamp && !Overlays && Crop == null && Texts.Count == 0 && Strokes.Count == 0;

    public static string LookName(Look l) => l switch
    {
        Look.BlackWhite => "Black & white",
        Look.Retro => "Retro 8-bit",
        _ => l.ToString(),
    };
}

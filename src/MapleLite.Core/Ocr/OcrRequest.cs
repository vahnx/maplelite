using MapleLite.Core.Imaging;

namespace MapleLite.Core.Ocr;

/// <summary>How a crop is enlarged before OCR.</summary>
public enum OcrScaling
{
    /// <summary>Bilinear.</summary>
    Smooth,
    /// <summary>Repeated pixels: hard pixel fonts stay crisp.</summary>
    Nearest,
    /// <summary>Bicubic (Catmull-Rom): sharper edges than bilinear, close to System.Drawing's HighQualityBicubic.</summary>
    Bicubic,
}

public enum OcrGrouping
{
    /// <summary>Lines exactly as the engine reports them.</summary>
    EngineLines,
    /// <summary>
    /// Words regrouped into rows by their vertical center. Engines sometimes split one chat line
    /// in two or merge neighbours; rows follow what is on screen.
    /// </summary>
    Rows,
}

/// <summary>
/// What to read and how. The defaults suit most game text: auto-enlarge up to 3x with smoothing.
/// </summary>
public sealed record OcrRequest(Region Region)
{
    /// <summary>Enlarge factor. 0 picks the largest of 1 to 3 that the engine accepts.</summary>
    public int Scale { get; init; }
    public OcrScaling Scaling { get; init; } = OcrScaling.Smooth;
    /// <summary>Margin of repeated edge pixels added around the region before enlarging (helps text touching the edge).</summary>
    public int Pad { get; init; }
    /// <summary>Cleanup run on the crop at its original size, before enlarging.</summary>
    public OcrPreprocessor Preprocess { get; init; } = OcrPreprocessor.None;
    public OcrGrouping Grouping { get; init; } = OcrGrouping.EngineLines;
    /// <summary>
    /// When true (the default), identical reads of the same frame are done once and shared,
    /// even between plugins. Turn off only for reads that must not be reused.
    /// </summary>
    public bool Cache { get; init; } = true;

    internal string Key => $"{Region}|{Scale}|{Scaling}|{Pad}|{Preprocess.Key}|{Grouping}";
}

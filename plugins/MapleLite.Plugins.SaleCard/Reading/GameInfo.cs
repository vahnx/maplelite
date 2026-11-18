using System.Text.RegularExpressions;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;

namespace MapleLite.Plugins.SaleCard;

/// <summary>Short names for the parts of a shared-OCR line the readers use (all in frame coordinates).</summary>
internal static class Lines
{
    public static double Cy(this OcrLine l) => l.Box.CenterY;
    public static double Left(this OcrLine l) => l.Box.Left;
    public static double Right(this OcrLine l) => l.Box.Right;
}


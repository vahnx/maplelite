using MapleLite.Core.Ocr;

namespace MapleLite.Core.Game;

/// <summary>
/// An item of the MapleStory Classic catalog: its game id, the name the game shows, and a URL slug. The shared
/// <see cref="ItemCatalog"/> (GameData/Items/items.json) is made of these; the field names match that file.
/// </summary>
public sealed class CatalogItem
{
#pragma warning disable IDE1006
    public int id { get; set; }
    public string slug { get; set; } = "";
    public string name { get; set; } = "";
#pragma warning restore IDE1006
}

/// <summary>Short names for the parts of an OCR line the game readers use (all in frame coordinates).</summary>
internal static class LineParts
{
    public static double Cy(this OcrLine l) => l.Box.CenterY;
    public static double Left(this OcrLine l) => l.Box.Left;
    public static double Right(this OcrLine l) => l.Box.Right;
}

using System.Text.RegularExpressions;

namespace MapleLite.Core.Game;

/// <summary>The map you are on, read from the minimap by MapleLite (one reader shared by every plugin).</summary>
public interface IMapSource
{
    /// <summary>The map name without its room number ("Free Market"), or null until it has been read.</summary>
    string? Map { get; }
    /// <summary>Why the map can't be read right now (minimap at its middle size, or not showing), or null when it can.</summary>
    string? Problem { get; }
}

/// <summary>
/// Settles the map name from a stream of minimap reads (MapleLite's <see cref="GameWatcher"/> feeds it). A new name is
/// taken only once it reads the same twice in a row (one odd read is OCR), and the last name is kept while the minimap
/// can't be read (collapsed to its middle size, or covered).
/// </summary>
public sealed partial class MapWatcher
{
    string? _candidate;

    /// <summary>The map you're on, or null until it's been read.</summary>
    public string? Map { get; private set; }

    /// <summary>The map name without its room number ("Free Market &lt;1&gt;" → "Free Market"), so every room files together.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var name = RoomSuffix().Replace(text, "").Trim(' ', '-', ':');
        return name.Count(char.IsLetter) >= 3 ? name : null;
    }

    /// <summary>Takes one read; returns the map name to use from now on.</summary>
    public string? Take(string? read)
    {
        var name = Clean(read);
        if (name == null) return Map;
        if (string.Equals(name, Map, StringComparison.OrdinalIgnoreCase)) { _candidate = null; return Map; }
        if (string.Equals(name, _candidate, StringComparison.OrdinalIgnoreCase)) { Map = name; _candidate = null; }
        else _candidate = name;
        return Map;
    }

    [GeneratedRegex(@"\s*[<(\[]\s*\d{1,3}\s*[>)\]]\s*$")]
    private static partial Regex RoomSuffix();
}

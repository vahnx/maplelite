using System.Text.Json;
using System.Text.Json.Serialization;
using MapleLite.Sdk;

namespace MapleLite.Plugins.Camera.Model;

public enum MediaKind { Photo, Video, Frames, Gif }

/// <summary>
/// What MapleLite knows about one capture, kept beside the plugin's data (not in the pictures folder):
/// when it was taken, the game's display scale, the map, what the overlay showed, and the edit in progress.
/// </summary>
public sealed class MediaInfo
{
    public DateTimeOffset Taken { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public double Fps { get; set; }
    public double Seconds { get; set; }
    public double GameScale { get; set; } = 1;
    public string? Map { get; set; }
    /// <summary>For photos: what the other plugins drew over the game at that moment.</summary>
    public List<OverlayItem>? Overlays { get; set; }
    public Edit? Edit { get; set; }
}

/// <summary>Overlay items over time, for clips: one line per change.</summary>
public sealed record OverlayAt(double Ms, List<OverlayItem> Items);

/// <summary>One capture in the pictures folder.</summary>
public sealed record MediaItem(string Path, MediaKind Kind, DateTime Modified)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsClip => Kind is MediaKind.Video or MediaKind.Frames;
}

/// <summary>
/// The pictures folder (Pictures/MapleLite by default) and MapleLite's notes about each capture in it.
/// Snapshots are lossless PNGs; clips are MP4s (with ffmpeg) or "*.frames" folders of full-color JPEGs (without);
/// exports get " (edited)" names so originals are never replaced.
/// </summary>
public sealed class Library
{
    public const string FramesSuffix = ".frames";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    readonly string _notes;
    public string Folder { get; private set; }

    public Library(string folder, string notesFolder)
    {
        Folder = folder;
        _notes = Path.Combine(notesFolder, "media");
        Directory.CreateDirectory(_notes);
    }

    public static string DefaultFolder()
    {
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(pics)) pics = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        return Path.Combine(pics, "MapleLite");
    }

    public void MoveTo(string folder) => Folder = folder;

    /// <summary>A new name in the folder: "MapleLite 2026-11-19 21-04-33.png" (with " 2", " 3"… if taken).</summary>
    public string NewPath(DateTimeOffset time, string extension, string? suffix = null)
    {
        Directory.CreateDirectory(Folder);
        var stem = $"MapleLite {time:yyyy-MM-dd HH-mm-ss}{suffix}";
        var path = Path.Combine(Folder, stem + extension);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++) path = Path.Combine(Folder, $"{stem} {i}{extension}");
        return path;
    }

    /// <summary>Where an edited copy of <paramref name="original"/> goes.</summary>
    public string EditedPath(string original, string extension)
    {
        var stem = Path.GetFileNameWithoutExtension(original.TrimEnd(Path.DirectorySeparatorChar));
        if (stem.EndsWith(FramesSuffix, StringComparison.OrdinalIgnoreCase)) stem = stem[..^FramesSuffix.Length];
        var path = Path.Combine(Folder, $"{stem} (edited){extension}");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(Folder, $"{stem} (edited {i}){extension}");
        return path;
    }

    public List<MediaItem> List(int max = 60)
    {
        var list = new List<MediaItem>();
        if (!Directory.Exists(Folder)) return list;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Folder))
            {
                var kind = Path.GetExtension(f).ToLowerInvariant() switch
                {
                    ".png" or ".jpg" or ".jpeg" => MediaKind.Photo,
                    ".mp4" => MediaKind.Video,
                    ".gif" => MediaKind.Gif,
                    _ => (MediaKind?)null,
                };
                if (kind is { } k) list.Add(new MediaItem(f, k, File.GetLastWriteTime(f)));
            }
            foreach (var d in Directory.EnumerateDirectories(Folder, "*" + FramesSuffix))
                if (Recording.IsRecording(d)) list.Add(new MediaItem(d, MediaKind.Frames, Directory.GetLastWriteTime(d)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return list.OrderByDescending(m => m.Modified).Take(max).ToList();
    }

    string NotePath(string media, string ext) => Path.Combine(_notes, Path.GetFileName(media.TrimEnd(Path.DirectorySeparatorChar)) + ext);
    public string ThumbPath(string media) => NotePath(media, ".thumb.jpg");

    public MediaInfo? Info(string media)
    {
        try { return JsonSerializer.Deserialize<MediaInfo>(File.ReadAllText(NotePath(media, ".json")), Json); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void SaveInfo(string media, MediaInfo info)
    {
        try { File.WriteAllText(NotePath(media, ".json"), JsonSerializer.Serialize(info, Json)); }
        catch (IOException) { }
    }

    public void SaveOverlays(string media, IEnumerable<OverlayAt> timeline)
    {
        try { File.WriteAllLines(NotePath(media, ".overlays.jsonl"), timeline.Select(o => JsonSerializer.Serialize(o))); }
        catch (IOException) { }
    }

    public List<OverlayAt> Overlays(string media)
    {
        var list = new List<OverlayAt>();
        var path = NotePath(media, ".overlays.jsonl");
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            try { if (JsonSerializer.Deserialize<OverlayAt>(line) is { } o) list.Add(o); }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>Removes a capture and MapleLite's notes about it (to the OS trash is not available cross-platform, so this deletes).</summary>
    public void Delete(MediaItem item)
    {
        if (item.Kind == MediaKind.Frames) Directory.Delete(item.Path, true);
        else File.Delete(item.Path);
        foreach (var ext in new[] { ".json", ".thumb.jpg", ".overlays.jsonl" })
        {
            try { File.Delete(NotePath(item.Path, ext)); } catch (IOException) { }
        }
    }
}

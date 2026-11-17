using System.Text.Json;
using System.Text.Json.Serialization;
using MapleLite.Core.Game;

namespace MapleLite.Plugins.Loot;

/// <summary>One pickup. Source says where it was seen: "OCR · notice" (the pickup notices) or "chat".</summary>
public sealed record LootRecord(DateTimeOffset Time, string Kind, string Name, long Quantity, string Text, string Source)
{
    [JsonIgnore] public LootDrop Drop => new(Kind, Name, Quantity);
    [JsonIgnore] public string When => Time.ToLocalTime().ToString("MMM dd  HH:mm:ss");

    public static LootRecord From(DateTimeOffset time, LootDrop d, string text, string source) => new(time, d.Kind, d.Name, d.Quantity, text, source);
}

public sealed class LootSettings
{
    /// <summary>True once the MapleMemory app's pickup history has been offered for import.</summary>
    public bool ImportChecked { get; set; }
}

/// <summary>The pickup log: monthly loot-YYYY-MM.jsonl files, one record per line, in the plugin's data folder.</summary>
public sealed class LootLog
{
    public string Folder { get; }
    public readonly List<LootRecord> Records = [];
    public int UnreadableLines { get; private set; }

    public LootLog(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        foreach (var path in Directory.EnumerateFiles(folder, "loot-*.jsonl").Order())
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                try { if (JsonSerializer.Deserialize<LootRecord>(line) is { } r) Records.Add(r); }
                catch (JsonException) { UnreadableLines++; }
            }
        Records.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    public void Add(LootRecord r)
    {
        File.AppendAllText(Path.Combine(Folder, $"loot-{r.Time:yyyy-MM}.jsonl"), JsonSerializer.Serialize(r) + Environment.NewLine);
        Records.Add(r);
    }

    /// <summary>Where the MapleMemory app keeps its journal (Windows only).</summary>
    public static string AppFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapleMemory");

    /// <summary>
    /// Copies pickups ("Loot" rows) out of MapleMemory-format journals (messages-*.jsonl). Rows already in the log
    /// (same time and text) are skipped, so importing twice adds nothing. The journals are only read.
    /// </summary>
    public int ImportJournals(string journalFolder)
    {
        if (!Directory.Exists(journalFolder)) return 0;
        var have = new HashSet<(DateTimeOffset, string)>(Records.Select(r => (r.Time, r.Text)));
        int added = 0;
        foreach (var path in Directory.EnumerateFiles(journalFolder, "messages-*.jsonl").Order())
            foreach (var line in File.ReadLines(path))
            {
                if (!line.Contains("\"Loot\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.GetProperty("Channel").GetString() != "Loot") continue;
                    var time = root.GetProperty("Time").GetDateTimeOffset();
                    var text = root.GetProperty("Text").GetString() ?? "";
                    if (GameText.Pickup(text) is not { } drop || !have.Add((time, text))) continue;
                    var source = root.TryGetProperty("Source", out var s) && s.GetString() == "OCR · loot" ? "OCR · notice" : "chat";
                    Add(LootRecord.From(time, drop, text, source));
                    added++;
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
            }
        if (added > 0) Records.Sort((a, b) => a.Time.CompareTo(b.Time));
        return added;
    }
}

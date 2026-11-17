using Avalonia.Controls;
using MapleLite.Sdk;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.MapleMemory;

/// <summary>
/// MapleMemory as a plugin (port in progress, milestone 3). Will move here: chat OCR via the shared
/// reader (OcrPreprocessor.ChatGlyphs + OcrGrouping.Rows), ChatDetector, ChatLayout, ScrollTracker,
/// MessageSettler, Classifier, loot/trade/fame detection, the journal (messages-YYYY-MM.jsonl), filters and CSV.
/// Existing journals in %LOCALAPPDATA%/MapleMemory will be imported, not moved.
/// </summary>
public sealed class MapleMemoryPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new("maplememory", "MapleMemory", "0.1.0", "CheesyFrog Technologies",
        "Chat journal: a searchable log of chat, whispers, trades, fame and loot.");

    IPluginContext? _ctx;

    public Task StartAsync(IPluginContext context) { _ctx = context; return Task.CompletedTask; }
    public Task StopAsync() => Task.CompletedTask;

    public Control? CreatePanel() => Card(Stack(8,
        Title("Port in progress"),
        Muted("The chat journal moves here next. Until then, keep using the MapleMemory app; its journal will be imported."),
        Muted($"Shared text reader: {_ctx?.Ocr.EngineName ?? "–"}")));
}

using Avalonia.Controls;
using MapleLite.Sdk;

namespace MapleLite.Plugins.ChatHistory;

/// <summary>
/// Chat History (formerly the MapleMemory app), the chat journal, as a MapleLite plugin. Reads the chat box (and trade dialogs and pickup
/// notices) from the host's game frames with the host's shared OCR, and keeps a searchable local log.
/// Existing MapleMemory app history is imported (copied) on first start; the app's files are left alone.
/// </summary>
public sealed class ChatHistoryPlugin : IPlugin
{
    MemoryEngine? _engine;

    public PluginManifest Manifest { get; } = new("chathistory", "Chat History", "1.0.0", "CheesyFrog Technologies",
        "Chat journal: a searchable log of chat, whispers, trades, fame and loot.");

    public async Task StartAsync(IPluginContext context)
    {
        _engine = new MemoryEngine(context);
        await _engine.AutoStartAsync();
    }

    public async Task StopAsync()
    {
        if (_engine != null) await _engine.StopAsync();
    }

    public Control? CreatePanel() => _engine == null ? null : new MemoryPanel(_engine);
}

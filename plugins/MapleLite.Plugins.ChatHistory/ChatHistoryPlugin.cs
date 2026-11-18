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

    public PluginManifest Manifest { get; } = new("chathistory", "Chat History", "0.1.0-alpha", "CheesyFrog Technologies",
        "Chat journal: a searchable log of chat, whispers, trades, fame and loot.");

    public Task StartAsync(IPluginContext context)
    {
        _engine = new MemoryEngine(context);
        // Finding the chat box reads the screen with OCR (over a second): do it after startup instead of holding
        // up MapleLite's window and the other plugins. It runs on the UI thread, where the engine's timers live.
        var engine = _engine;
        context.OnUi(async () =>
        {
            try { await engine.AutoStartAsync(); }
            catch (Exception e) { context.Log("chat reading didn't start: " + e.Message); }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_engine != null) await _engine.StopAsync();
    }

    public Control? CreatePanel() => _engine == null ? null : new MemoryPanel(_engine);
}

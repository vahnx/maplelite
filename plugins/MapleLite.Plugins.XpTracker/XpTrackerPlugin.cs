using Avalonia.Controls;
using MapleLite.Sdk;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.XpTracker;

/// <summary>
/// XP tracker (in development). First version: XP/hour and time to level, saved sessions with CSV export,
/// and a live rate graph. EXP is read from the status bar with the shared OCR; the math lives in
/// <see cref="ExpParser"/> and <see cref="XpRate"/>. Locating the EXP text needs saved captures of the
/// status bar at a few resolutions before it's wired to live frames.
/// </summary>
public sealed class XpTrackerPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new("xptracker", "XP Tracker", "0.1.0", "CheesyFrog Technologies",
        "EXP per hour, time to the next level, session history and a rate graph.");

    public Task StartAsync(IPluginContext context) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;

    public Control? CreatePanel() => Card(Stack(8,
        Title("In development"),
        Muted("Planned for the first version: EXP per hour, time to level, saved sessions with CSV export, and a live rate graph."),
        Muted("Next step: saved pictures of the EXP bar at a few game resolutions, so the reader can be checked offline.")));
}

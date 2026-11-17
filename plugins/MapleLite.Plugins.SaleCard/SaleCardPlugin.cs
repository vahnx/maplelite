using Avalonia.Controls;
using MapleLite.Sdk;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.SaleCard;

/// <summary>
/// MapleStory Sale Card Generator as a plugin (port in progress, milestone 4). Will move here: inventory,
/// shop, equipment and tooltip readers (on the shared OCR), price sources read from pictures of open
/// browser tabs, the item catalog, the price book, and the card renderer (rewritten on SkiaSharp).
/// Data files keep their formats: session.json, fm-prices.json, site-prices.json, pricebook.json,
/// hover-stats.json, not-for-sale.json.
/// </summary>
public sealed class SaleCardPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new("salecard", "Sale Card", "0.1.0", "CheesyFrog Technologies",
        "Reads your inventory and shop, tracks prices, and makes a shareable sale card.");

    public Task StartAsync(IPluginContext context) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;

    public Control? CreatePanel() => Card(Stack(8,
        Title("Port in progress"),
        Muted("The sale card generator moves here after MapleMemory. Until then, keep using the MapleStory Sale Card Generator app.")));
}

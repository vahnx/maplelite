using Avalonia.Controls;
using MapleLite.Sdk;

namespace MapleLite.Plugins.SaleCard;

/// <summary>
/// The MapleStory Sale Card Generator, as a MapleLite plugin. Reads your inventory, storage, hovered tooltips and the
/// player shops you browse from the host's game frames with the host's shared OCR; keeps going rates from FM shops and
/// (when you turn it on) price pages open in your own browser; and makes a shareable sale card PNG.
/// Data files keep the app's formats (session.json, fm-prices.json, site-prices.json, pricebook.json,
/// hover-stats.json, not-for-sale.json) in plugins/salecard, and Settings → Import copies the app's Data folder in.
/// </summary>
public sealed class SaleCardPlugin : IPlugin
{
    SaleCardEngine? _engine;
    IPluginContext? _context;

    public PluginManifest Manifest { get; } = new("salecard", "Shop Card", "0.1.0-alpha", "CheesyFrog Technologies",
        "Reads your inventory and shops, tracks going rates, and makes a shareable card of what you sell and buy.");

    public async Task StartAsync(IPluginContext context)
    {
        _context = context;
        _engine = new SaleCardEngine(context);
        await _engine.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_engine != null) await _engine.StopAsync();
    }

    public Control? CreatePanel() => _engine == null || _context == null ? null : new SaleCardPanel(_engine, _context);
}

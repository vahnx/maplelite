using Avalonia.Controls;
using MapleLite.Sdk;

namespace MapleLite.Plugins.DpsSimulator;

public sealed class DpsSimulatorPlugin : IPlugin
{
    readonly CancellationTokenSource _lifetime = new();
    IPluginContext? _context;
    IDisposable? _frames;
    public PluginManifest Manifest { get; } = new(EquippedLoadout.PluginId, "DPS Simulator", "0.1.0-alpha", "CheesyFrog Technologies",
        "Read equipped gear, plan upgrades and estimate Classic World damage against a monster.");

    public Task StartAsync(IPluginContext context)
    {
        _context = context;
        _frames = context.Frames.Subscribe(_ => { }, 1);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _lifetime.Cancel();
        _frames?.Dispose();
        return Task.CompletedTask;
    }

    public Control? CreatePanel() => _context == null ? null : new DpsPanel(_context, _lifetime.Token);
}

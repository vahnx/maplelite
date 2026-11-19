using Avalonia.Controls;
using MapleLite.Core.Capture;
using MapleLite.Core.Ocr;
using MapleLite.Sdk.Theming;

namespace MapleLite.Sdk;

public static class SdkInfo
{
    /// <summary>Plugins built against a different API version are not loaded.</summary>
    public const int ApiVersion = 1;
}

/// <summary>Describes a plugin. Id is permanent: it names the plugin's data folder.</summary>
public sealed record PluginManifest(string Id, string Name, string Version, string Author, string Description)
{
    public int ApiVersion { get; init; } = SdkInfo.ApiVersion;
}

/// <summary>
/// A MapleLite plugin. The host makes an instance, calls <see cref="StartAsync"/>, asks for its panel,
/// and calls <see cref="StopAsync"/> when the plugin is switched off or the app exits. Switching it on again
/// makes a fresh instance. Keep the constructor cheap: the host also creates one just to read the manifest.
/// Frame and event subscriptions made through the context are cleaned up by the host on stop.
///
/// What plugins get: game frames, the shared OCR, the item catalog, storage, themes, notifications, hotkeys.
/// What plugins never get: input to the game, game memory, packets, or their own screen capture.
/// MapleLite is passive; plugins that automate play are not accepted.
/// </summary>
public interface IPlugin
{
    PluginManifest Manifest { get; }
    Task StartAsync(IPluginContext context);
    Task StopAsync();
    /// <summary>The plugin's page in the host window. Called on the UI thread, once.</summary>
    Control? CreatePanel();
}

public interface IPluginContext
{
    PluginManifest Manifest { get; }
    /// <summary>The shared text reader. Reads of the same frame and region are shared with other plugins.</summary>
    IOcr Ocr { get; }
    /// <summary>Game frames from the host's single capture loop.</summary>
    IFrameSource Frames { get; }
    /// <summary>The map you are on, read from the minimap once for all plugins (don't read the minimap yourself).</summary>
    MapleLite.Core.Game.IMapSource Map { get; }
    IPluginStorage Storage { get; }
    IThemeService Themes { get; }
    /// <summary>Events shared between plugins (chat lines, chat box position, …).</summary>
    IEventBus Events { get; }
    /// <summary>
    /// The game's item catalog, shared by all plugins: ids, names, slugs and each item's inventory icon. Shipped with
    /// the app; empty when its files are missing.
    /// </summary>
    MapleLite.Core.Game.ItemCatalog Items { get; }
    /// <summary>The mouse position in the game's client area, when known (for tooltip readers).</summary>
    (int X, int Y)? CursorInGame();
    /// <summary>
    /// The folder the plugin was installed from (its .dll and any files shipped beside it, such as an Assets folder).
    /// Read-only: save data in <see cref="Storage"/>.
    /// </summary>
    string PluginFolder { get; }
    /// <summary>
    /// Pictures of other windows the user has open, such as a browser tab showing a price site. Read only from
    /// pictures; nothing is sent to those windows. Plugins should only use this when the user turned it on.
    /// </summary>
    IOtherWindows OtherWindows { get; }
    /// <summary>Puts a picture on the clipboard. False when this OS isn't supported yet (offer saving a file instead).</summary>
    Task<bool> CopyImageAsync(MapleLite.Core.Imaging.Frame image);
    /// <summary>Labels drawn over the game window, when the user has overlays turned on. See <see cref="IOverlay"/>.</summary>
    IOverlay Overlay { get; }
    /// <summary>System-wide keyboard shortcuts (for actions taken while playing). See <see cref="IHotkeys"/>.</summary>
    IHotkeys Hotkeys { get; }
    /// <summary>A short message in the host's notification area.</summary>
    void Notify(string message);
    void Log(string message);
    /// <summary>Runs an action on the UI thread (for updating panels from capture or OCR threads).</summary>
    void OnUi(Action action);
}

/// <summary>Other top-level windows, read only from their pictures (never input, never memory).</summary>
public interface IOtherWindows
{
    /// <summary>Visible windows with a title, not counting MapleLite's own.</summary>
    IReadOnlyList<MapleLite.Core.Platform.WindowCandidate> List();
    /// <summary>A picture of the whole window (also when other windows cover it), or null when it can't be read.</summary>
    Task<MapleLite.Core.Imaging.Frame?> CaptureAsync(nint handle, CancellationToken ct = default);
}

/// <summary>A private folder per plugin, plus simple JSON settings.</summary>
public interface IPluginStorage
{
    string Folder { get; }
    T Load<T>(string name) where T : new();
    void Save<T>(string name, T value);
}

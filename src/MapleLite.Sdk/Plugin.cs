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
/// A MapleLite plugin. The host makes one instance, calls <see cref="StartAsync"/>, asks for its panel,
/// and calls <see cref="StopAsync"/> on exit.
///
/// What plugins get: game frames, the shared OCR, storage, themes, notifications.
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
    IPluginStorage Storage { get; }
    IThemeService Themes { get; }
    /// <summary>The mouse position in the game's client area, when known (for tooltip readers).</summary>
    (int X, int Y)? CursorInGame();
    /// <summary>A short message in the host's notification area.</summary>
    void Notify(string message);
    void Log(string message);
    /// <summary>Runs an action on the UI thread (for updating panels from capture or OCR threads).</summary>
    void OnUi(Action action);
}

/// <summary>A private folder per plugin, plus simple JSON settings.</summary>
public interface IPluginStorage
{
    string Folder { get; }
    T Load<T>(string name) where T : new();
    void Save<T>(string name, T value);
}

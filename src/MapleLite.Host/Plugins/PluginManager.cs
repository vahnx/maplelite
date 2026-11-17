using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Threading;
using MapleLite.Core;
using MapleLite.Core.Capture;
using MapleLite.Core.Ocr;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host.Plugins;

internal sealed record LoadedPlugin(IPlugin Plugin, PluginContext Context, string Path)
{
    public string? StartError { get; set; }
}

/// <summary>
/// Loads plugins from plugins/&lt;Name&gt;/&lt;Name&gt;.dll, each in its own AssemblyLoadContext.
/// Core, Sdk and Avalonia always resolve to the host's copies, so plugin and host share those types.
/// In-process plugins are trusted code: the future hub builds third-party plugins from reviewed source.
/// </summary>
internal sealed class PluginManager
{
    readonly List<LoadedPlugin> _plugins = new();
    readonly List<string> _errors = new();
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public IReadOnlyList<string> Errors => _errors;

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "plugins");

    public void LoadAll(Shell shell)
    {
        if (!Directory.Exists(Folder)) { _errors.Add($"No plugins folder at {Folder}."); return; }
        foreach (var dir in Directory.EnumerateDirectories(Folder).Order())
        {
            var name = Path.GetFileName(dir);
            var dll = Path.Combine(dir, name + ".dll");
            if (!File.Exists(dll)) { _errors.Add($"{name}: {name}.dll is missing."); continue; }
            try
            {
                var alc = new PluginLoadContext(dll);
                var asm = alc.LoadFromAssemblyPath(dll);
                var types = asm.GetTypes().Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }).ToList();
                if (types.Count == 0) { _errors.Add($"{name}: no IPlugin class found."); continue; }
                foreach (var type in types)
                {
                    var plugin = (IPlugin)Activator.CreateInstance(type)!;
                    var m = plugin.Manifest;
                    if (m.ApiVersion != SdkInfo.ApiVersion) { _errors.Add($"{m.Name}: built for plugin API {m.ApiVersion}; this MapleLite has API {SdkInfo.ApiVersion}."); continue; }
                    if (_plugins.Any(p => p.Plugin.Manifest.Id == m.Id)) { _errors.Add($"{m.Name}: another plugin already uses the id \"{m.Id}\"."); continue; }
                    if (shell.Settings.DisabledPlugins.Contains(m.Id)) continue;
                    _plugins.Add(new LoadedPlugin(plugin, new PluginContext(m, shell), dll));
                }
            }
            catch (Exception e) when (e is BadImageFormatException or FileLoadException or ReflectionTypeLoadException or TypeLoadException or MissingMethodException or TargetInvocationException)
            {
                _errors.Add($"{name}: couldn't load ({e.GetType().Name}: {e.Message}).");
            }
        }
    }

    public async Task StartAllAsync()
    {
        foreach (var p in _plugins)
        {
            try { await p.Plugin.StartAsync(p.Context); }
            catch (Exception e) { p.StartError = e.Message; p.Context.Log("start failed: " + e); }
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var p in _plugins)
        {
            try { await p.Plugin.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) { p.Context.Log("stop failed: " + e.Message); }
        }
    }
}

internal sealed class PluginLoadContext(string mainAssembly) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssembly))
{
    readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

    protected override Assembly? Load(AssemblyName name)
    {
        // Anything the host ships (Core, Sdk, Avalonia, ...) comes from the host so types match.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"))) return null;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : 0;
    }
}

internal sealed class PluginContext(PluginManifest manifest, Shell shell) : IPluginContext
{
    public PluginManifest Manifest { get; } = manifest;
    public IOcr Ocr => shell.Ocr;
    public IFrameSource Frames => shell.Capture;
    public IPluginStorage Storage { get; } = new PluginStorage(AppPaths.PluginData(manifest.Id));
    public IThemeService Themes => shell.Themes;

    public (int X, int Y)? CursorInGame() =>
        shell.Capture.Status.Window is { } w ? shell.Platform.Cursor.PositionIn(w) : null;

    public void Notify(string message) => shell.Notify($"{Manifest.Name}: {message}");
    public void Log(string message) => shell.Log($"[{Manifest.Id}] {message}");
    public void OnUi(Action action) => Dispatcher.UIThread.Post(action);
}

internal sealed class PluginStorage(string folder) : IPluginStorage
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string Folder { get; } = folder;

    string PathOf(string name) => Path.Combine(Folder, Path.GetFileName(name) + ".json");

    public T Load<T>(string name) where T : new()
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(PathOf(name))) ?? new T(); }
        catch (Exception e) when (e is IOException or JsonException) { return new T(); }
    }

    public void Save<T>(string name, T value)
    {
        var p = PathOf(name);
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(value, Options));
        File.Move(p + ".tmp", p, true);
    }
}

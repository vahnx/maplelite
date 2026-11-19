using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Threading;
using MapleLite.Core;
using MapleLite.Core.Capture;
using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host.Plugins;

/// <summary>A plugin the host knows about, running or not.</summary>
internal sealed class PluginEntry(PluginManifest manifest, Type type, string folder, bool bundled)
{
    public PluginManifest Manifest { get; } = manifest;
    public Type Type { get; } = type;
    public string Folder { get; } = folder;
    /// <summary>Shipped with MapleLite: can be switched off, not removed.</summary>
    public bool Bundled { get; } = bundled;
    public IPlugin? Instance { get; set; }
    public PluginContext? Context { get; set; }
    public string? Error { get; set; }
    public bool Running => Instance != null;
    /// <summary>Changes each time the plugin is (re)started, so pages made for an old instance are dropped.</summary>
    public int Generation { get; set; }
}

/// <summary>
/// Finds, starts, stops, installs and removes plugins.
/// Bundled plugins: plugins/&lt;Name&gt;/&lt;Name&gt;.dll next to the app. Added plugins: installed-plugins/&lt;Name&gt;/ in
/// the data folder. Each loads in its own AssemblyLoadContext, from memory (so the files are never locked and
/// removal works at once). Core, Sdk and Avalonia always come from the host, so plugin and host share types.
/// In-process plugins are trusted code: they run with the user's access. The future hub will build them from
/// reviewed source; until then, adding one shows a warning.
/// </summary>
internal sealed class PluginManager(Shell shell)
{
    readonly List<PluginEntry> _entries = new();
    readonly List<string> _errors = new();
    public IReadOnlyList<PluginEntry> Entries => _entries;
    public IReadOnlyList<string> Errors => _errors;
    /// <summary>Raised (on the UI thread) when a plugin starts, stops, is added or removed.</summary>
    public event Action? Changed;

    public static string BundledFolder => Path.Combine(AppContext.BaseDirectory, "plugins");
    public static string InstalledFolder => AppPaths.Sub("installed-plugins");

    public void Discover()
    {
        _entries.Clear(); _errors.Clear();
        foreach (var (root, bundled) in new[] { (BundledFolder, true), (InstalledFolder, false) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.')).Order())
            {
                var (found, error) = Inspect(dir, bundled);
                if (error != null) _errors.Add($"{Path.GetFileName(dir)}: {error}");
                foreach (var e in found)
                {
                    if (_entries.Any(x => x.Manifest.Id == e.Manifest.Id)) { _errors.Add($"{e.Manifest.Name}: another plugin already uses the id \"{e.Manifest.Id}\"."); continue; }
                    _entries.Add(e);
                }
            }
        }
    }

    /// <summary>Loads the plugin assembly in a folder and returns its plugins, or why it can't be used.</summary>
    static (List<PluginEntry> found, string? error) Inspect(string dir, bool bundled)
    {
        var name = Path.GetFileName(dir);
        var dll = Path.Combine(dir, name + ".dll");
        if (!File.Exists(dll))
        {
            // an added plugin's folder may be named differently from its assembly: take the one with a plugin in it
            dll = Directory.EnumerateFiles(dir, "*.dll").FirstOrDefault(p => !HostHas(Path.GetFileNameWithoutExtension(p))) ?? "";
            if (dll.Length == 0) return ([], "no plugin .dll in the folder.");
        }
        try
        {
            var alc = new PluginLoadContext(dll);
            var asm = alc.LoadMain();
            var types = asm.GetTypes().Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }).ToList();
            if (types.Count == 0) return ([], "no IPlugin class found.");
            var found = new List<PluginEntry>();
            foreach (var type in types)
            {
                var probe = (IPlugin)Activator.CreateInstance(type)!;
                var m = probe.Manifest;
                if (m.ApiVersion != SdkInfo.ApiVersion) return (found, $"{m.Name} was built for plugin API {m.ApiVersion}; this MapleLite has API {SdkInfo.ApiVersion}.");
                if (string.IsNullOrWhiteSpace(m.Id) || m.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return (found, $"{m.Name} has an unusable id \"{m.Id}\".");
                found.Add(new PluginEntry(m, type, dir, bundled));
            }
            return (found, null);
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException or ReflectionTypeLoadException or TypeLoadException or MissingMethodException or TargetInvocationException or InvalidCastException)
        {
            return ([], $"couldn't load ({e.GetType().Name}: {e.Message}).");
        }
    }

    internal static bool HostHas(string assemblyName) => File.Exists(Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll"));

    public bool IsEnabled(PluginEntry e) => !shell.Settings.DisabledPlugins.Contains(e.Manifest.Id);

    /// <summary>Starts every enabled plugin, one at a time. <paramref name="starting"/> hears (plugin, index, count) before each.</summary>
    public async Task StartEnabledAsync(Func<PluginEntry, int, int, Task>? starting = null)
    {
        var list = _entries.Where(IsEnabled).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (starting != null) await starting(list[i], i, list.Count);
            await StartAsync(list[i]);
        }
        Changed?.Invoke();
    }

    async Task StartAsync(PluginEntry e)
    {
        if (e.Running) return;
        e.Error = null;
        try
        {
            var instance = (IPlugin)Activator.CreateInstance(e.Type)!;
            var ctx = new PluginContext(e.Manifest, shell, e.Folder);
            e.Instance = instance; e.Context = ctx; e.Generation++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await instance.StartAsync(ctx);
            shell.Log($"[{e.Manifest.Id}] started in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            e.Error = ex.Message;
            shell.Log($"[{e.Manifest.Id}] start failed: {ex}");
        }
    }

    /// <summary>Stops a plugin, giving it <paramref name="timeout"/> (default 5 s) to save. Returns what went wrong, or null.</summary>
    async Task<string?> StopAsync(PluginEntry e, TimeSpan? timeout = null)
    {
        if (!e.Running) return null;
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        string? problem = null;
        try { await e.Instance!.StopAsync().WaitAsync(limit); }
        catch (TimeoutException) { problem = $"didn't finish within {limit.TotalSeconds:0} s, so it was skipped"; }
        catch (Exception ex) { problem = "failed: " + ex.Message; }
        if (problem != null) shell.Log($"[{e.Manifest.Id}] stop {problem}");
        e.Context?.Close();
        e.Instance = null; e.Context = null;
        return problem;
    }

    /// <summary>Switches a plugin on or off now, and remembers the choice.</summary>
    public async Task SetEnabledAsync(PluginEntry e, bool on)
    {
        shell.Settings.DisabledPlugins.Remove(e.Manifest.Id);
        if (!on) shell.Settings.DisabledPlugins.Add(e.Manifest.Id);
        shell.Settings.Save();
        if (on) await StartAsync(e); else await StopAsync(e);
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds a plugin from a .zip (its files) or a .dll (copied with its .pdb and .deps.json). The plugin is checked
    /// before it is kept: it must contain an IPlugin for this API version with an id not already in use.
    /// Returns a message for the user.
    /// </summary>
    public async Task<(bool ok, string message)> InstallAsync(string path)
    {
        var staging = Path.Combine(InstalledFolder, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(path, staging); // rejects entries that would escape the folder
                // a zip with one top folder: use what's inside it
                var dirs = Directory.GetDirectories(staging);
                if (Directory.GetFiles(staging).Length == 0 && dirs.Length == 1)
                {
                    var inner = dirs[0];
                    var flat = staging + "-flat";
                    Directory.Move(inner, flat);
                    Directory.Delete(staging, true);
                    Directory.Move(flat, staging);
                }
            }
            else if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
                foreach (var ext in new[] { ".dll", ".pdb", ".deps.json" })
                    if (File.Exists(stem + ext)) File.Copy(stem + ext, Path.Combine(staging, Path.GetFileName(stem) + ext));
            }
            else return (false, "Choose a plugin .zip or .dll file.");

            var (found, error) = Inspect(staging, bundled: false);
            if (error != null || found.Count == 0) return (false, "That isn't a usable MapleLite plugin: " + (error ?? "no plugin found."));
            var clash = found.FirstOrDefault(f => _entries.Any(x => x.Manifest.Id == f.Manifest.Id));
            if (clash != null) return (false, $"A plugin with the id \"{clash.Manifest.Id}\" is already installed. Remove it first to replace it.");

            var assemblyName = found[0].Type.Assembly.GetName().Name!;
            var target = Path.Combine(InstalledFolder, assemblyName);
            if (Directory.Exists(target)) return (false, $"A plugin folder named {assemblyName} already exists. Remove that plugin first.");
            Directory.Move(staging, target);

            var (installed, _) = Inspect(target, bundled: false);
            foreach (var e in installed) { _entries.Add(e); shell.Settings.DisabledPlugins.Remove(e.Manifest.Id); await StartAsync(e); }
            shell.Settings.Save();
            Changed?.Invoke();
            return (true, $"Added {string.Join(", ", installed.Select(e => e.Manifest.Name))}.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (false, "Couldn't add the plugin: " + e.Message);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
        }
    }

    /// <summary>Stops an added plugin and deletes its files. Its saved data (plugins/&lt;id&gt;) is kept.</summary>
    public async Task<string> RemoveAsync(PluginEntry e)
    {
        if (e.Bundled) return $"{e.Manifest.Name} comes with MapleLite; switch it off instead.";
        foreach (var sibling in _entries.Where(x => x.Folder == e.Folder).ToList())
        {
            await StopAsync(sibling);
            _entries.Remove(sibling);
            shell.Settings.DisabledPlugins.Remove(sibling.Manifest.Id);
        }
        shell.Settings.Save();
        string message;
        try { Directory.Delete(e.Folder, true); message = $"Removed {e.Manifest.Name}. Its saved data is still in the data folder."; }
        catch (IOException ex) { message = $"Removed {e.Manifest.Name}, but couldn't delete its files: {ex.Message}"; }
        Changed?.Invoke();
        return message;
    }

    /// <summary>
    /// Stops every running plugin so each can save. <paramref name="stopping"/> hears (plugin, index, count) before
    /// each; <paramref name="stopped"/> hears how long it took and what went wrong, if anything.
    /// </summary>
    public async Task StopAllAsync(Func<PluginEntry, int, int, Task>? stopping = null, Action<PluginEntry, TimeSpan, string?>? stopped = null, TimeSpan? timeout = null)
    {
        var list = _entries.Where(e => e.Running).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (stopping != null) await stopping(list[i], i, list.Count);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var problem = await StopAsync(list[i], timeout);
            stopped?.Invoke(list[i], sw.Elapsed, problem);
        }
    }
}

/// <summary>
/// One plugin assembly's load context. Everything is loaded from bytes, so plugin files are never locked.
/// Assemblies the host ships (Core, Sdk, Avalonia, …) resolve to the host's copies.
/// </summary>
internal sealed class PluginLoadContext(string mainAssembly) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssembly))
{
    readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

    public Assembly LoadMain() => FromBytes(mainAssembly);

    Assembly FromBytes(string path)
    {
        var pdb = Path.ChangeExtension(path, ".pdb");
        using var asm = new MemoryStream(File.ReadAllBytes(path));
        if (!File.Exists(pdb)) return LoadFromStream(asm);
        using var sym = new MemoryStream(File.ReadAllBytes(pdb));
        return LoadFromStream(asm, sym);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (PluginManager.HostHas(name.Name ?? "")) return null;
        var path = _resolver.ResolveAssemblyToPath(name)
                   ?? Path.Combine(Path.GetDirectoryName(mainAssembly)!, name.Name + ".dll");
        return File.Exists(path) ? FromBytes(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : 0;
    }
}

/// <summary>
/// What a plugin gets. Frame, status and event subscriptions are tracked and removed when the plugin stops,
/// so a plugin that forgets to unsubscribe can't keep running after it's switched off.
/// </summary>
internal sealed class PluginContext : IPluginContext
{
    readonly Shell _shell;
    readonly List<IDisposable> _tracked = new();
    readonly TrackedFrames _frames;
    readonly TrackedEvents _events;
    bool _closed;

    public PluginContext(PluginManifest manifest, Shell shell, string folder)
    {
        Manifest = manifest; _shell = shell; PluginFolder = folder;
        OtherWindows = new WindowPictures(shell.Platform);
        Storage = new PluginStorage(AppPaths.PluginData(manifest.Id));
        _frames = new TrackedFrames(shell.Capture, Track);
        _events = new TrackedEvents(shell.Events, Track);
    }

    public PluginManifest Manifest { get; }
    public IOcr Ocr => _shell.Ocr;
    public IFrameSource Frames => _frames;
    public MapleLite.Core.Game.IMapSource Map => _shell.Game;
    public MapleLite.Core.Game.IGameState Game => _shell.Game;
    public IPluginStorage Storage { get; }
    public IThemeService Themes => _shell.Themes;
    public IEventBus Events => _events;
    public ItemCatalog Items => _shell.Items;

    public (int X, int Y)? CursorInGame()
    {
        if (_shell.Capture.Status.Window is not { } w || _shell.Platform.Cursor.PositionIn(w) is not { } p) return null;
        // frames have letterbox bars cut off: measure from the same corner they do
        var off = _shell.Capture.Latest?.Offset ?? default;
        return (p.X - off.X, p.Y - off.Y);
    }

    public string PluginFolder { get; }
    public IOtherWindows OtherWindows { get; }
    PluginOverlay? _overlay;
    public IOverlay Overlay => _overlay ??= new PluginOverlay(_shell.Overlay, Manifest.Id);
    PluginHotkeys? _hotkeys;
    public IHotkeys Hotkeys => _hotkeys ??= new PluginHotkeys(_shell.Hotkeys, Manifest.Id, Track);

    public async Task<bool> CopyImageAsync(Frame image)
    {
        if (_shell.Platform.Clipboard is not { } clip) return false;
        await clip.SetImageAsync(image, FrameFiles.EncodePng(image, opaque: false));
        return true;
    }

    public void Notify(string message) { if (!_closed) _shell.Notify($"{Manifest.Name}: {message}"); }
    public void Log(string message) => _shell.Log($"[{Manifest.Id}] {message}");
    public void OnUi(Action action) { if (!_closed) Dispatcher.UIThread.Post(() => { if (!_closed) action(); }); }

    IDisposable Track(IDisposable d)
    {
        lock (_tracked)
        {
            if (_closed) { d.Dispose(); return d; }
            _tracked.Add(d);
        }
        return d;
    }

    public void Close()
    {
        IDisposable[] all;
        lock (_tracked) { _closed = true; all = _tracked.ToArray(); _tracked.Clear(); }
        foreach (var d in all) { try { d.Dispose(); } catch { } }
        _overlay?.Close();
    }

    sealed class Callback(Action a) : IDisposable { public void Dispose() => a(); }

    sealed class WindowPictures(IPlatform platform) : IOtherWindows
    {
        public IReadOnlyList<WindowCandidate> List()
        {
            try { return platform.Windows.TopLevelWindows(); }
            catch (Exception) { return []; }
        }

        public async Task<Frame?> CaptureAsync(nint handle, CancellationToken ct = default)
        {
            try { return await platform.Capture.CaptureWindowAsync(handle, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; }
        }
    }

    sealed class TrackedFrames(IFrameSource inner, Func<IDisposable, IDisposable> track) : IFrameSource
    {
        public CaptureStatus Status => inner.Status;
        public GameFrame? Latest => inner.Latest;
        public IDisposable Subscribe(Action<GameFrame> onFrame, double fps) => track(inner.Subscribe(onFrame, fps));
        public event Action<CaptureStatus>? StatusChanged
        {
            add { if (value == null) return; inner.StatusChanged += value; track(new Callback(() => inner.StatusChanged -= value)); }
            remove { if (value != null) inner.StatusChanged -= value; }
        }
    }

    sealed class TrackedEvents(IEventBus inner, Func<IDisposable, IDisposable> track) : IEventBus
    {
        public void Publish<T>(T evt) where T : class => inner.Publish(evt);
        public IDisposable Subscribe<T>(Action<T> handler) where T : class => track(inner.Subscribe(handler));
        public T? Last<T>() where T : class => inner.Last<T>();
    }
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

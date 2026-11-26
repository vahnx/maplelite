using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MapleLite.Core.Capture;
using MapleLite.Core.Ocr;
using MapleLite.Plugins.PlayerFinder;
using MapleLite.Sdk;
using Xunit;

namespace MapleLite.UiTests;

public class PlayerFinderTests
{
    [AvaloniaFact]
    public async Task Watchlist_notes_autosave_and_flush_the_last_edit_on_stop()
    {
        var storage = new Storage();
        storage.Save("settings", new { Names = new[] { "Alice", "Bob" } });
        storage.Save("notes", new Dictionary<string, string> { ["alice"] = "Old note" });
        using var ocr = new OcrService(null);
        var context = DispatchProxy.Create<IPluginContext, Context>();
        ((Context)(object)context).Storage = storage;
        ((Context)(object)context).Ocr = ocr;
        var plugin = new PlayerFinderPlugin();
        await plugin.StartAsync(context);
        var window = new Window { Content = plugin.CreatePanel() };
        try
        {
            window.Show();
            TextBox NoteFor(string name) => window.GetVisualDescendants().OfType<TextBox>()
                .Single(box => AutomationProperties.GetName(box) == $"Note for {name}");
            var alice = NoteFor("Alice");
            var bob = NoteFor("Bob");
            Assert.Equal("Old note", alice.Text);

            bob.Focus();
            bob.Text = "Party quest\nUsually on channel 2";
            for (int i = 0; i < 100 && !storage.Notes.ContainsKey("Bob"); i++) await Task.Delay(20);
            Assert.Equal(bob.Text, storage.Notes["Bob"]);

            alice.Text = "";
        }
        finally
        {
            await plugin.StopAsync();
            window.Close();
        }
        Assert.Equal("Bob", Assert.Single(storage.Notes).Key);
    }

    public class Context : DispatchProxy
    {
        public IPluginStorage Storage = null!;
        public IOcr Ocr = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Storage" => Storage,
            "get_Ocr" => Ocr,
            "get_Frames" => new EmptyFrames(),
            "get_Overlay" => DispatchProxy.Create<IOverlay, Context>(),
            "Clear" or "OnUi" => null,
            _ => throw new NotSupportedException(method.Name),
        };
    }

    sealed class EmptyFrames : IFrameSource, IDisposable
    {
        public CaptureStatus Status => CaptureStatus.Stopped;
        public GameFrame? Latest => null;
        public event Action<CaptureStatus>? StatusChanged { add { } remove { } }
        public IDisposable Subscribe(Action<GameFrame> onFrame, double fps) => this;
        public void Dispose() { }
    }

    sealed class Storage : IPluginStorage
    {
        readonly Dictionary<string, string> _values = new();
        public string Folder => "";
        public Dictionary<string, string> Notes => Load<Dictionary<string, string>>("notes");
        public T Load<T>(string name) where T : new() => _values.TryGetValue(name, out var json)
            ? JsonSerializer.Deserialize<T>(json) ?? new() : new();
        public void Save<T>(string name, T value) => _values[name] = JsonSerializer.Serialize(value);
    }
}

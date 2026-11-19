using Avalonia.Controls;
using MapleLite.Core.Capture;
using MapleLite.Core.Ocr;
using MapleLite.Sdk;
using static MapleLite.Sdk.PanelKit;

namespace HelloPlugin;

/// <summary>
/// The smallest useful plugin: shows the game picture size, the last chat line Chat History saw (via the event bus),
/// and a button that reads the top-left corner of the game with the shared OCR.
/// </summary>
public sealed class HelloPlugin : IPlugin
{
    IPluginContext? _ctx;
    string _lastChat = "nothing yet (turn on Chat History)";

    public PluginManifest Manifest { get; } = new("hello", "Hello (Developer)", "1.0.0", "You", "A sample plugin to copy when writing your own.");

    public Task StartAsync(IPluginContext context)
    {
        _ctx = context;
        // Subscriptions made through the context are cleaned up by MapleLite when the plugin is switched off.
        context.Events.Subscribe<ChatMessageEvent>(e => _lastChat = $"[{e.Channel}] {e.Text}");
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    public Control? CreatePanel()
    {
        if (_ctx == null) return null;
        var ctx = _ctx;
        var info = Muted("");
        var read = Text("");
        void Refresh()
        {
            var f = ctx.Frames.Latest;
            info.Text = (f == null ? "No game picture yet." : $"Game picture: {f.Frame.W}×{f.Frame.H}") + $"\nLast chat line: {_lastChat}";
        }
        var button = Button("Read the top-left of the game", async () =>
        {
            if (ctx.Frames.Latest is not { } gf) { read.Text = "No game picture yet."; return; }
            var r = await ctx.Ocr.ReadAsync(gf.Frame, new MapleLite.Core.Imaging.Region(0, 0, Math.Min(400, gf.Frame.W), Math.Min(60, gf.Frame.H)));
            read.Text = r.Lines.Count == 0 ? "No text there." : r.Text;
            Refresh();
        }, accent: true);
        Refresh();
        return Card(Stack(10, Title("Hello from a plugin"), info, button, read));
    }
}

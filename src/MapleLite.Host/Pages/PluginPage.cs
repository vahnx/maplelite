using Avalonia.Controls;
using MapleLite.Host.Plugins;
using MapleLite.Sdk.Theming;

namespace MapleLite.Host.Pages;

internal static class PluginPage
{
    public static Control Create(LoadedPlugin p)
    {
        var m = p.Plugin.Manifest;
        Control? panel = null;
        string? error = p.StartError;
        if (error == null)
        {
            try { panel = p.Plugin.CreatePanel(); }
            catch (Exception e) { error = "Its page failed to open: " + e.Message; p.Context.Log("panel failed: " + e); }
        }
        var header = Ui.Stack(4, Ui.Heading(m.Name), Ui.Muted($"{m.Description}  ·  v{m.Version} by {m.Author}"));
        var body = Ui.Stack(18, header);
        if (error != null) body.Children.Add(Ui.Card(Ui.Text("This plugin had a problem. " + error, ThemeTokens.Danger)));
        if (panel != null) body.Children.Add(panel);
        else if (error == null) body.Children.Add(Ui.Card(Ui.Muted("This plugin has no page.")));
        return Ui.Page(body);
    }
}

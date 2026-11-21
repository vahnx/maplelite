using Avalonia.Controls;
using MapleLite.Core.Market;

namespace MapleLite.Host.Pages;

internal sealed class MarketPage : UserControl
{
    readonly TextBox _item = new() { Watermark = "Item ID (blank = all)" };
    readonly TextBlock _results = Ui.Muted("");
    bool _busy;
    public MarketPage()
    {
        Content = Ui.Page(Ui.Stack(12, Ui.Heading("Market database"),
            Ui.Muted("Local asking-price observations, not confirmed sales. Archived every 30 seconds from Shop Card. Legacy records have an unknown server. Uploads are not configured."),
            Ui.Muted(MarketDatabase.DefaultPath),
            Ui.Row(8, _item, Ui.Button("Refresh", async () => await Refresh()), Ui.Button("Open database folder", () =>
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(MarketDatabase.DefaultPath)!) { UseShellExecute = true }))),
            _results));
        AttachedToVisualTree += async (_, _) => await Refresh();
    }
    async Task Refresh()
    {
        if (_busy) return;
        int? item = null;
        if (!string.IsNullOrWhiteSpace(_item.Text))
        {
            if (!int.TryParse(_item.Text, out int id) || id <= 0) { _results.Text = "Enter a positive item ID."; return; }
            item = id;
        }
        _busy = true;
        try
        {
            var rows = await Task.Run(() => new MarketDatabase(MarketDatabase.DefaultPath).Recent(item));
            _results.Text = rows.Count == 0 ? "No observations archived yet. Browse FM shops with Shop Card enabled." :
                string.Join("\n\n", rows.Select(r => $"Item {r.ItemId} · {r.Source} · {r.Server} · {r.Utc:u}\n"
                    + (r.Source == "FM" ? $"Asking {r.UnitPrice:N0} each · bundle {r.Bundle} · {r.Stats} {r.Slots}" :
                        $"Last {r.Last:N0} · Avg 3 {r.Average3:N0} · Median 10 {r.Median10:N0} · Typical {r.Typical:N0}")));
        }
        catch (Exception e) { _results.Text = "Couldn't read market database: " + e.Message; }
        finally { _busy = false; }
    }
}

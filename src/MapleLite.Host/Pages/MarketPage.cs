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
            Ui.Muted("Local asking prices and historical observations. MapleLite archives Shop Card prices every 30 seconds. Legacy records have an unknown server."),
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
            var (prices, rows) = await Task.Run(() =>
            {
                var db = new MarketDatabase(MarketDatabase.DefaultPath);
                return (db.AskingPrices(item), db.Recent(item));
            });
            _results.Text = prices.Count == 0 && rows.Count == 0 ? "No prices archived yet. Set a Shop Card price or browse FM shops." :
                string.Join("\n\n", prices.Select(p => $"Item {p.ItemId} · Shop Card asking price\n{p.Price}" +
                    (p.Profile.Length > 0 ? $" · {p.Profile.Replace("|", " · ")}" : ""))
                    .Concat(rows.Select(r => $"Item {r.ItemId} · {r.Source} · {r.Server} · {r.Utc:u}\n"
                    + (r.Source == "FM" ? $"Asking {r.UnitPrice:N0} each · bundle {r.Bundle} · {r.Stats} {r.Slots}" :
                        $"Last {r.Last:N0} · Avg 3 {r.Average3:N0} · Median 10 {r.Median10:N0} · Typical {r.Typical:N0}"))));
        }
        catch (Exception e) { _results.Text = "Couldn't read market database: " + e.Message; }
        finally { _busy = false; }
    }
}

using System.Text.Json;
using MapleLite.Core;
using MapleLite.Core.Market;

namespace MapleLite.Host;

/// <summary>Indexes existing price files off the UI thread. Leaves original data intact during staged migration.</summary>
internal sealed class MarketArchive : IAsyncDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly Task _task;
    readonly Dictionary<string, string> _previous = new();
    public MarketArchive(Action<string> log) => _task = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { Index(); }
            catch (Exception e) { log("Market archive: " + e.Message); }
        } while (await timer.WaitForNextTickAsync(_stop.Token));
    });

    void Index()
    {
        var database = new MarketDatabase(MarketDatabase.DefaultPath);
        var priceBook = Path.Combine(AppPaths.PluginData("salecard"), "pricebook.json");
        if (File.Exists(priceBook) || File.Exists(database.Path))
        {
            var json = File.Exists(priceBook) ? File.ReadAllText(priceBook) : "{}";
            if (_previous.GetValueOrDefault(priceBook) != json)
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                var prices = new List<SavedAskingPrice>();
                foreach (var (key, price) in saved)
                {
                    var parts = key.Split(':', 2);
                    if (int.TryParse(parts[0], out int id) && id > 0 && !string.IsNullOrWhiteSpace(price))
                        prices.Add(new(id, parts.Length == 2 ? parts[1] : "", price));
                }
                database.ReplaceAskingPrices(prices);
                _previous[priceBook] = json;
            }
        }
        foreach (var name in new[] { "fm-prices.json", "site-prices.json" })
        {
            var file = Path.Combine(AppPaths.PluginData("salecard"), name);
            if (!File.Exists(file)) continue;
            string json = File.ReadAllText(file);
            if (_previous.GetValueOrDefault(name) == json) continue;
            using var doc = JsonDocument.Parse(json);
            var rows = new List<MarketObservation>();
            static long Number(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.GetInt64() : 0;
            static string Text(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.GetString() ?? "" : "";
            if (name.StartsWith("fm-"))
                foreach (var e in doc.RootElement.EnumerateArray())
                    rows.Add(new((int)Number(e, "Id"), "FM", "unknown", e.GetProperty("Utc").GetDateTime(),
                        Number(e, "Unit"), (int)Number(e, "Bundle"), Text(e, "Seller"), Text(e, "Stats"), Text(e, "Slots")));
            else
                foreach (var site in doc.RootElement.EnumerateObject())
                    foreach (var item in site.Value.EnumerateObject())
                    {
                        var e = item.Value;
                        rows.Add(new(int.Parse(item.Name), site.Name, "unknown", e.GetProperty("Utc").GetDateTime(),
                            Last: Number(e, "Last"), Average3: Number(e, "Avg3"), Median10: Number(e, "Median10"), Typical: Number(e, "Typical")));
                    }
            database.Append(rows);
            _previous[name] = json;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _task; } catch (OperationCanceledException) { }
        // Plugins have saved before shutdown reaches here; archive their final observations too.
        try { await Task.Run(Index); } catch { /* originals remain intact; next start retries */ }
        _stop.Dispose();
    }
}

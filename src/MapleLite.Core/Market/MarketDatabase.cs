using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MapleLite.Core.Market;

public sealed record MarketObservation(int ItemId, string Source, string Server, DateTime Utc,
    long UnitPrice = 0, int Bundle = 1, string Seller = "", string Stats = "", string Slots = "",
    long Last = 0, long Average3 = 0, long Median10 = 0, long Typical = 0);

public sealed record SavedAskingPrice(int ItemId, string Profile, string Price);

/// <summary>Append-only price history and a read-only index of current Shop Card asking prices.</summary>
public sealed class MarketDatabase
{
    public static string DefaultPath => System.IO.Path.Combine(AppPaths.Sub("market"), "prices.sqlite");
    public string Path { get; }
    public MarketDatabase(string path) => Path = path;

    SqliteConnection Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, DefaultTimeout = 2 }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS observations (
              observation_id TEXT PRIMARY KEY, item_id INTEGER NOT NULL, source TEXT NOT NULL,
              server TEXT NOT NULL, observed_utc TEXT NOT NULL, unit_price INTEGER NOT NULL,
              bundle INTEGER NOT NULL, seller TEXT NOT NULL, stats TEXT NOT NULL, slots TEXT NOT NULL,
              last_sale INTEGER NOT NULL, average_3 INTEGER NOT NULL, median_10 INTEGER NOT NULL, typical INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS observations_item ON observations(item_id, server, source, observed_utc);
            CREATE TABLE IF NOT EXISTS card_prices (
              item_id INTEGER NOT NULL, profile TEXT NOT NULL, price TEXT NOT NULL, PRIMARY KEY (item_id, profile));
            CREATE VIEW IF NOT EXISTS website_prices AS
              SELECT item_id, server, source, observed_utc, last_sale, average_3, median_10, typical
              FROM observations WHERE source != 'FM';
            CREATE VIEW IF NOT EXISTS fm_prices AS
              SELECT item_id, server, observed_utc, unit_price, bundle, seller, stats, slots
              FROM observations WHERE source = 'FM';
            PRAGMA user_version=2;
            """;
        try { command.ExecuteNonQuery(); return db; }
        catch { db.Dispose(); throw; }
    }

    public void ReplaceAskingPrices(IEnumerable<SavedAskingPrice> prices)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM card_prices";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO card_prices (item_id,profile,price) VALUES ($id,$profile,$price)";
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var profile = command.Parameters.Add("$profile", SqliteType.Text);
        var price = command.Parameters.Add("$price", SqliteType.Text);
        foreach (var row in prices)
        {
            id.Value = row.ItemId;
            profile.Value = row.Profile;
            price.Value = row.Price;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<SavedAskingPrice> AskingPrices(int? itemId = null)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT item_id,profile,price FROM card_prices WHERE ($id IS NULL OR item_id=$id) ORDER BY item_id,profile LIMIT 500";
        command.Parameters.AddWithValue("$id", (object?)itemId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<SavedAskingPrice>();
        while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    public void Append(IEnumerable<MarketObservation> observations)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO observations VALUES ($key,$item,$source,$server,$time,$unit,$bundle,$seller,$stats,$slots,$last,$avg,$median,$typical)";
        foreach (var row in observations)
        {
            if (row.ItemId <= 0 || row.Bundle <= 0 || row.UnitPrice < 0 || row.Last < 0 || row.Average3 < 0 || row.Median10 < 0 || row.Typical < 0)
                throw new ArgumentException("Invalid market observation.");
            command.Parameters.Clear();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row))));
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$item", row.ItemId);
            command.Parameters.AddWithValue("$source", row.Source);
            command.Parameters.AddWithValue("$server", row.Server);
            command.Parameters.AddWithValue("$time", row.Utc.ToString("O"));
            command.Parameters.AddWithValue("$unit", row.UnitPrice);
            command.Parameters.AddWithValue("$bundle", row.Bundle);
            command.Parameters.AddWithValue("$seller", row.Seller);
            command.Parameters.AddWithValue("$stats", row.Stats);
            command.Parameters.AddWithValue("$slots", row.Slots);
            command.Parameters.AddWithValue("$last", row.Last);
            command.Parameters.AddWithValue("$avg", row.Average3);
            command.Parameters.AddWithValue("$median", row.Median10);
            command.Parameters.AddWithValue("$typical", row.Typical);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<MarketObservation> Recent(int? itemId = null)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT item_id,source,server,observed_utc,unit_price,bundle,seller,stats,slots,last_sale,average_3,median_10,typical FROM observations WHERE ($id IS NULL OR item_id=$id) ORDER BY observed_utc DESC LIMIT 500";
        command.Parameters.AddWithValue("$id", (object?)itemId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<MarketObservation>();
        while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.GetInt64(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12)));
        return rows;
    }

    /// <summary>Per-item estimates from the latest usable observations, preferring saved item-wide asks.</summary>
    public IReadOnlyDictionary<int, long> ItemPriceEstimates()
    {
        var prices = new Dictionary<int, long>();
        if (!File.Exists(Path)) return prices;

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            WITH ranked AS (
                SELECT item_id,
                    COALESCE(NULLIF(unit_price, 0), NULLIF(typical, 0), NULLIF(median_10, 0),
                             NULLIF(last_sale, 0), average_3) AS price,
                    ROW_NUMBER() OVER (PARTITION BY item_id ORDER BY observed_utc DESC, observation_id) AS rank
                FROM observations
                WHERE unit_price > 0 OR typical > 0 OR median_10 > 0 OR last_sale > 0 OR average_3 > 0
            )
            SELECT item_id, price FROM ranked WHERE rank = 1
            """;
        using (var reader = command.ExecuteReader())
            while (reader.Read()) prices[reader.GetInt32(0)] = reader.GetInt64(1);

        command.CommandText = "SELECT item_id, price FROM card_prices WHERE profile = ''";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (MesoAmount.Parse(reader.GetString(1)) is > 0 and var price) prices[reader.GetInt32(0)] = price;

        return prices;
    }
}

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

// Independent of trade retention: rejected proposals are valuable labelled examples.
public sealed class ReviewArchiveStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReviewArchiveStore(string? databasePath = null)
    {
        var queuePath = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json");
        databasePath ??= Path.Combine(Path.GetDirectoryName(queuePath)!, "review-archive.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    public async Task SaveAsync(string key, SignalCandidate candidate, IReadOnlyList<BybitKline> candles,
        string interval, bool lineMode, byte[] image, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO reviews(id,created_utc,delivery,decision,metadata,candles,image) VALUES($id,$at,'Prepared','Unanswered',$metadata,$candles,$image)";
            command.Parameters.AddWithValue("$id", key);
            command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(new
            {
                candidate, timeframe = interval, chartMode = lineMode ? "Line" : "Candles",
                version = "review-v1", entryDistancePercent = Math.Abs(candidate.LastPrice - candidate.EntryPrice) / candidate.EntryPrice * 100m
            }, Json));
            command.Parameters.AddWithValue("$candles", JsonSerializer.Serialize(candles, Json));
            command.Parameters.AddWithValue("$image", image);
            await command.ExecuteNonQueryAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkDeliveryAsync(string key, bool sent, CancellationToken token) =>
        await ChangeAsync(key, "UPDATE reviews SET delivery=$value WHERE id=$id", sent ? "Sent" : "Failed", token);

    public async Task<bool> DecideAsync(string key, bool accepted, CancellationToken token) =>
        await ChangeAsync(key,
            "UPDATE reviews SET decision=$value,decided_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$id AND decision='Unanswered'",
            accepted ? "Approved" : "Rejected", token) > 0;

    public async Task LinkSignalAsync(string key, Guid? signalId, CancellationToken token) =>
        await ChangeAsync(key, "UPDATE reviews SET signal_id=$value WHERE id=$id", signalId?.ToString() ?? "SubmissionFailed", token);

    private async Task<int> ChangeAsync(string key, string sql, string value, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", key);
            command.Parameters.AddWithValue("$value", value);
            return await command.ExecuteNonQueryAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]> ExportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken token)
    {
        if (toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(31))
            throw new ArgumentException("بازه آرشیو باید حداکثر ۳۱ روز باشد.");
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,created_utc,delivery,decision,decided_utc,metadata,candles,image,signal_id FROM reviews WHERE created_utc >= $from AND created_utc < $to ORDER BY created_utc LIMIT 2001";
            command.Parameters.AddWithValue("$from", fromUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$to", toUtc.ToUniversalTime().ToString("O"));
            using var output = new MemoryStream();
            var manifest = new List<object>();
            long bytes = 0;
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var image = (byte[])reader[7];
                    var candles = reader.GetString(6);
                    bytes += image.Length + Encoding.UTF8.GetByteCount(candles);
                    if (manifest.Count >= 2000 || bytes > 128 * 1024 * 1024)
                        throw new ArgumentException("حجم آرشیو زیاد است؛ بازه تاریخ کوتاه‌تری انتخاب کنید.");
                    var id = reader.GetString(0);
                    var decision = reader.GetString(3);
                    var folder = $"{decision}/{id}";
                    Write(archive, folder + ".png", image);
                    Write(archive, folder + "-candles.json", Encoding.UTF8.GetBytes(candles));
                    var metadata = JsonSerializer.Deserialize<JsonElement>(reader.GetString(5));
                    var item = new
                    {
                        id, createdAtUtc = reader.GetString(1), delivery = reader.GetString(2), decision,
                        decidedAtUtc = reader.IsDBNull(4) ? null : reader.GetString(4),
                        signalId = reader.IsDBNull(8) ? null : reader.GetString(8), metadata,
                        image = folder + ".png", candles = folder + "-candles.json"
                    };
                    Write(archive, folder + ".json", JsonSerializer.SerializeToUtf8Bytes(item, Json));
                    manifest.Add(item);
                }
                Write(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
                Write(archive, "README.txt", Encoding.UTF8.GetBytes(
                    "Phoenix review dataset v1\nApproved/Rejected are user preference labels, NOT trading profitability.\nUnanswered is not a rejection; Prepared/Failed delivery must not be treated as reviewed.\nImages and candles capture proposal-time data. Do not use later outcome information as input features.\nSplit evaluation chronologically and group repeated symbol/anchor proposals to prevent leakage.\nDevelop any filter in shadow mode first; no automatic filter is enabled by this archive.\n"));
            }
            return output.ToArray();
        }
        finally { _gate.Release(); }
    }

    private static void Write(ZipArchive archive, string path, byte[] data)
    {
        using var stream = archive.CreateEntry(path, CompressionLevel.Fastest).Open();
        stream.Write(data);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS reviews(
                id TEXT PRIMARY KEY, created_utc TEXT NOT NULL, delivery TEXT NOT NULL,
                decision TEXT NOT NULL, decided_utc TEXT NULL, metadata TEXT NOT NULL,
                candles TEXT NOT NULL, image BLOB NOT NULL, signal_id TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_review_created ON reviews(created_utc);
            """;
        await command.ExecuteNonQueryAsync(token);
        return connection;
    }
}

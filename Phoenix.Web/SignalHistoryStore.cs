using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Phoenix.Web;

public sealed class SignalHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SignalHistoryStore(string queuePath)
    {
        var databasePath = Environment.GetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH")
            ?? Path.Combine(Path.GetDirectoryName(queuePath)!, "phoenix-history.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task UpsertAsync(ServerSignal signal, string fallbackEvent, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            var previous = await ReadSnapshotAsync(connection, signal.Id, token);
            var eventType = previous is null ? fallbackEvent : DetectEvent(previous, signal);
            var now = DateTime.UtcNow;
            var payload = JsonSerializer.Serialize(signal, JsonOptions);

            await using var transaction = await connection.BeginTransactionAsync(token);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO signals (id, symbol, direction, status, created_at_utc, updated_at_utc, removed_at_utc, payload)
                VALUES ($id, $symbol, $direction, $status, $created, $updated, NULL, $payload)
                ON CONFLICT(id) DO UPDATE SET
                    symbol = excluded.symbol, direction = excluded.direction, status = excluded.status,
                    updated_at_utc = excluded.updated_at_utc, payload = excluded.payload;
                """;
            Add(command, "$id", signal.Id.ToString());
            Add(command, "$symbol", signal.Symbol);
            Add(command, "$direction", signal.Direction);
            Add(command, "$status", signal.Status);
            Add(command, "$created", signal.CreatedAtUtc.ToString("O"));
            Add(command, "$updated", now.ToString("O"));
            Add(command, "$payload", payload);
            await command.ExecuteNonQueryAsync(token);

            if (eventType is not null)
                await InsertEventAsync(connection, (SqliteTransaction)transaction, signal.Id, eventType, now, payload, token);
            await transaction.CommitAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkRemovedAsync(ServerSignal signal, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            var now = DateTime.UtcNow;
            var payload = JsonSerializer.Serialize(signal, JsonOptions);
            await using var transaction = await connection.BeginTransactionAsync(token);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE signals SET removed_at_utc=$removed, updated_at_utc=$removed, payload=$payload WHERE id=$id";
            Add(command, "$removed", now.ToString("O"));
            Add(command, "$payload", payload);
            Add(command, "$id", signal.Id.ToString());
            await command.ExecuteNonQueryAsync(token);
            await InsertEventAsync(connection, (SqliteTransaction)transaction, signal.Id, "RemovedFromQueue", now, payload, token);
            await transaction.CommitAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SignalHistoryItem>> GetAsync(int days, int limit, CancellationToken token = default)
    {
        days = Math.Clamp(days, 1, 3650);
        limit = Math.Clamp(limit, 1, 5000);
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload, updated_at_utc, removed_at_utc
                FROM signals WHERE created_at_utc >= $from
                ORDER BY created_at_utc DESC LIMIT $limit;
                """;
            Add(command, "$from", DateTime.UtcNow.AddDays(-days).ToString("O"));
            Add(command, "$limit", limit);
            var result = new List<SignalHistoryItem>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var signal = JsonSerializer.Deserialize<ServerSignal>(reader.GetString(0), JsonOptions);
                if (signal is null) continue;
                result.Add(new(signal, DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                    reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)).ToUniversalTime()));
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        if (!_initialized)
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS signals (
                    id TEXT PRIMARY KEY, symbol TEXT NOT NULL, direction TEXT NOT NULL, status TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, removed_at_utc TEXT NULL,
                    payload TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_signals_created ON signals(created_at_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_signals_symbol ON signals(symbol, created_at_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_signals_status ON signals(status, created_at_utc DESC);
                CREATE TABLE IF NOT EXISTS signal_events (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT, signal_id TEXT NOT NULL,
                    event_type TEXT NOT NULL, occurred_at_utc TEXT NOT NULL, payload TEXT NOT NULL,
                    FOREIGN KEY(signal_id) REFERENCES signals(id));
                CREATE INDEX IF NOT EXISTS ix_events_signal ON signal_events(signal_id, occurred_at_utc);
                """;
            await command.ExecuteNonQueryAsync(token);
            _initialized = true;
        }
        return connection;
    }

    private static async Task<ServerSignal?> ReadSnapshotAsync(SqliteConnection connection, Guid id, CancellationToken token)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM signals WHERE id=$id";
        Add(command, "$id", id.ToString());
        var payload = await command.ExecuteScalarAsync(token) as string;
        return payload is null ? null : JsonSerializer.Deserialize<ServerSignal>(payload, JsonOptions);
    }

    private static string? DetectEvent(ServerSignal before, ServerSignal after)
    {
        if (before.Status != after.Status) return $"Status:{before.Status}->{after.Status}";
        if (before.ExpireStage != after.ExpireStage) return $"ExpireStage:{before.ExpireStage}->{after.ExpireStage}";
        if (before.TargetReachedAtUtc != after.TargetReachedAtUtc) return "TargetReached";
        if (before.RiskFreeReachedAtUtc != after.RiskFreeReachedAtUtc) return "RiskFreeReached";
        if (before.RiskFreeClosedAtUtc != after.RiskFreeClosedAtUtc) return "RiskFreeClosed";
        if (before.StopLossReachedAtUtc != after.StopLossReachedAtUtc) return "StopLossReached";
        return null;
    }

    private static async Task InsertEventAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, string eventType, DateTime occurredAt, string payload, CancellationToken token)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO signal_events(signal_id,event_type,occurred_at_utc,payload) VALUES($id,$type,$at,$payload)";
        Add(command, "$id", id.ToString());
        Add(command, "$type", eventType);
        Add(command, "$at", occurredAt.ToString("O"));
        Add(command, "$payload", payload);
        await command.ExecuteNonQueryAsync(token);
    }

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}

public sealed record SignalHistoryItem(ServerSignal Signal, DateTime UpdatedAtUtc, DateTime? RemovedAtUtc);

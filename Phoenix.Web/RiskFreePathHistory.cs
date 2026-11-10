namespace Phoenix.Web;

internal sealed record RiskFreePathJob(ServerSignal Signal, string ClosedAtKey, long? NextCandle);
internal sealed record RiskFreePathProgress(long NextCandle, string? Outcome = null, long? ResolvedAt = null);

public sealed partial class SignalHistoryStore
{
    internal async Task<IReadOnlyList<RiskFreePathJob>> GetRiskFreePathsAsync(long now, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var db = await OpenAsync(token);
            using var command = db.CreateCommand();
            command.CommandText = """
                WITH risk_free AS (
                    SELECT id, payload, COALESCE(json_extract(payload,'$.riskFreeClosedAtUtc'),
                                                json_extract(payload,'$.completedAtUtc')) AS closed_at
                    FROM signals WHERE json_extract(payload,'$.outcome') = 'RiskFree'
                )
                SELECT s.payload, s.closed_at, p.next_candle
                FROM risk_free s LEFT JOIN risk_free_paths p
                  ON p.signal_id = s.id AND p.closed_at = s.closed_at
                WHERE s.closed_at IS NOT NULL AND p.outcome IS NULL
                  AND COALESCE(p.next_check, 0) <= $now
                ORDER BY COALESCE(p.next_check, 0), s.closed_at, s.id LIMIT 8;
                """;
            Add(command, "$now", now);
            var jobs = new List<RiskFreePathJob>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                if (System.Text.Json.JsonSerializer.Deserialize<ServerSignal>(reader.GetString(0), JsonOptions) is { } signal)
                    jobs.Add(new(signal, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            return jobs;
        }
        finally { _gate.Release(); }
    }

    internal async Task SaveRiskFreePathAsync(RiskFreePathJob job, RiskFreePathProgress progress,
        long nextCheck, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var db = await OpenAsync(token);
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO risk_free_paths (signal_id, closed_at, next_candle, next_check, outcome, resolved_at)
                SELECT id, $closed, $next, $check, $outcome, $resolved FROM signals
                WHERE id = $id AND json_extract(payload,'$.outcome') = 'RiskFree'
                  AND COALESCE(json_extract(payload,'$.riskFreeClosedAtUtc'),
                               json_extract(payload,'$.completedAtUtc')) = $closed
                ON CONFLICT(signal_id) DO UPDATE SET closed_at = excluded.closed_at,
                    next_candle = excluded.next_candle, next_check = excluded.next_check,
                    outcome = excluded.outcome, resolved_at = excluded.resolved_at
                WHERE risk_free_paths.closed_at != excluded.closed_at
                   OR (risk_free_paths.outcome IS NULL AND risk_free_paths.next_candle IS $previous);
                """;
            Add(command, "$id", job.Signal.Id.ToString());
            Add(command, "$closed", job.ClosedAtKey);
            Add(command, "$next", progress.NextCandle);
            Add(command, "$check", nextCheck);
            AddNullable(command, "$outcome", progress.Outcome);
            AddNullable(command, "$resolved", progress.ResolvedAt);
            AddNullable(command, "$previous", job.NextCandle);
            await command.ExecuteNonQueryAsync(token);
        }
        finally { _gate.Release(); }
    }
}

using Microsoft.Data.Sqlite;

namespace Phoenix.Web;

public sealed record MobileSignalEvent(string Id, string Kind, DateTime At);
public sealed record MobileFirebaseConfig(string AppId, string ApiKey, string ProjectId, string SenderId)
{
    public static MobileFirebaseConfig? FromEnvironment()
    {
        string Read(string key) => Environment.GetEnvironmentVariable("PHOENIX_FIREBASE_" + key) ?? "";
        var config = new MobileFirebaseConfig(Read("APP_ID"), Read("API_KEY"), Read("PROJECT_ID"), Read("SENDER_ID"));
        return new[] { config.AppId, config.ApiKey, config.ProjectId, config.SenderId }.All(x =>
            x.Length is > 0 and < 256 && System.Text.RegularExpressions.Regex.IsMatch(x, @"\A[A-Za-z0-9:_\-.]+\z")) ? config : null;
    }
}

public sealed partial class MobilePushService
{
    private static async Task InitializeOutboxAsync(SqliteConnection db, CancellationToken ct)
    {
        bool migrated = false, enrolled = false;
        using (var schema = db.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(mobile_devices)";
            using var reader = await schema.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) { migrated |= reader.GetString(1) == "enabled_since"; enrolled |= reader.GetString(1) == "enrollment"; }
        }
        using var cmd = db.CreateCommand();
        cmd.CommandText = (migrated ? "" : "ALTER TABLE mobile_devices ADD COLUMN enabled_since INTEGER NOT NULL DEFAULT 0; UPDATE mobile_devices SET enabled_since=updated;") + (enrolled ? "" : "ALTER TABLE mobile_devices ADD COLUMN enrollment TEXT NOT NULL DEFAULT '';") + """
            CREATE TABLE IF NOT EXISTS mobile_delivery(owner TEXT NOT NULL, device_id TEXT NOT NULL, event_id TEXT NOT NULL,
                event_at INTEGER NOT NULL, kind TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
                next_attempt INTEGER NOT NULL DEFAULT 0, sent INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(owner,device_id,event_id));
            CREATE TABLE IF NOT EXISTS mobile_state(key TEXT PRIMARY KEY,value INTEGER NOT NULL);
            INSERT OR IGNORE INTO mobile_state VALUES('since', $now);
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task QueueAsync(string owner, IEnumerable<MobileSignalEvent> events, DateTimeOffset now, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            using var transaction = db.BeginTransaction();
            foreach (var e in events)
            {
                if (e.At.Kind != DateTimeKind.Utc || !MobileSignalWorker.Kinds.Contains(e.Kind) || e.Id.Length > 120) continue;
                long at = new DateTimeOffset(e.At).ToUnixTimeMilliseconds();
                if (at < now.AddMinutes(-15).ToUnixTimeMilliseconds() || at > now.ToUnixTimeMilliseconds()) continue;
                using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO mobile_delivery(owner,device_id,event_id,event_at,kind)
                    SELECT owner,device_id,$e,$at,$kind FROM mobile_devices
                    WHERE owner=$o AND enabled_since<=$at AND $at>=(SELECT value FROM mobile_state WHERE key='since');
                    """;
                cmd.Parameters.AddWithValue("$o", owner); cmd.Parameters.AddWithValue("$e", e.Id);
                cmd.Parameters.AddWithValue("$at", at); cmd.Parameters.AddWithValue("$kind", e.Kind);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            using var clean = db.CreateCommand(); clean.Transaction = transaction;
            clean.CommandText = "DELETE FROM mobile_delivery WHERE event_at<$old OR NOT EXISTS(SELECT 1 FROM mobile_devices d WHERE d.owner=mobile_delivery.owner AND d.device_id=mobile_delivery.device_id AND d.enabled_since<=mobile_delivery.event_at)";
            clean.Parameters.AddWithValue("$old", now.AddDays(-1).ToUnixTimeMilliseconds());
            await clean.ExecuteNonQueryAsync(ct); transaction.Commit();
        }
        finally { gate.Release(); }
    }

    public async Task DeliverPendingAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!Configured) return;
        var jobs = new List<(string Owner, Guid Device, string Id, string Kind, int Attempts)>();
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); using var cmd = db.CreateCommand();
            cmd.CommandText = """
                DELETE FROM mobile_delivery WHERE kind IN ('Created','Cancelled');
                SELECT n.owner,n.device_id,n.event_id,n.kind,n.attempts FROM mobile_delivery n
                JOIN mobile_devices d ON d.owner=n.owner AND d.device_id=n.device_id AND d.enabled_since<=n.event_at
                WHERE n.sent=0 AND n.next_attempt<=$now AND n.event_at>=$since ORDER BY n.event_at LIMIT 50
                """;
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$since", now.AddMinutes(-15).ToUnixTimeMilliseconds());
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) jobs.Add((reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        }
        finally { gate.Release(); }
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            var success = false;
            try
            {
                var result = await SendCoreAsync(job.Owner, "signal", job.Id, job.Kind, job.Device, ct);
                success = result.Configured && result.Sent > 0 && result.Failed == 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or InvalidOperationException or IOException or KeyNotFoundException or OperationCanceledException) { }
            await gate.WaitAsync(ct);
            try
            {
                await using var db = await OpenAsync(ct); using var cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE mobile_delivery SET sent=$s,attempts=attempts+1,next_attempt=$next WHERE owner=$o AND device_id=$d AND event_id=$e";
                cmd.Parameters.AddWithValue("$s", success ? 1 : 0);
                cmd.Parameters.AddWithValue("$next", now.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(job.Attempts, 6)))).ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$o", job.Owner); cmd.Parameters.AddWithValue("$d", job.Device.ToString()); cmd.Parameters.AddWithValue("$e", job.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally { gate.Release(); }
        }
    }
}

public sealed class MobileSignalWorker(ServerOrderStore store, MobilePushService push, MobileSignalFeed feed, DedicatedTelegramOptions dedicated,
    ILogger<MobileSignalWorker> logger) : BackgroundService
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "NearEntry", "Opened", "FirstProtection", "RiskFreeReached", "Target", "StopLoss", "RiskFree", "Expired", "EntryError", "ManualClosed" };
    public static IEnumerable<MobileSignalEvent> Events(ServerSignal signal, string owner, DedicatedTelegramOptions dedicated, bool includeInitialExpiry = false, bool includeUnowned = false)
    {
        var own = string.Equals(signal.RequestedByUsername, owner, StringComparison.OrdinalIgnoreCase);
        if (!own && !(includeUnowned && string.IsNullOrWhiteSpace(signal.RequestedByUsername)) && (signal.PublicSignalNumber is null || dedicated.Owns(signal.RequestedByUsername))) yield break;
        if ((signal.ExpireAdjustedAtUtc ?? signal.EntryTriggeredAtUtc) is { } near)
            yield return new($"{signal.Id}:NearEntry","NearEntry",near);
        if(signal.EarlyProtectionAtUtc is { } first)
            yield return new($"{signal.Id}:FirstProtection","FirstProtection",first);
        if (signal.Origin!="ExternalPosition" && signal.FilledAtUtc is { } opened) yield return new($"{signal.Id}:Opened", "Opened", opened);
        if (signal.RiskFreeReachedAtUtc is { } riskFree) yield return new($"{signal.Id}:RiskFreeReached", "RiskFreeReached", riskFree);
        if(signal.FilledAtUtc is null && signal.Status is "Error" or "Rejected" or "Deactivated" or "RecoveryRequired" && signal.CompletedAtUtc is null)
            yield return new($"{signal.Id}:EntryError","EntryError",signal.EntryTriggeredAtUtc??signal.CreatedAtUtc);
        if (signal.CompletedAtUtc is { } end && signal.Outcome is { } outcome && Kinds.Contains(outcome) &&
            (outcome != "Expired" || includeInitialExpiry || signal.ExpireAdjustedAtUtc is not null || signal.EntryTriggeredAtUtc is not null))
            yield return new($"{signal.Id}:{outcome}", outcome, end);
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (PhoenixSessionAuth.CredentialsConfigured(out var owner, out _))
                {
                    await feed.CaptureAsync(owner,ct);
                    var signals = await store.GetAllAsync(ct);
                    await push.QueueAsync(owner, signals.SelectMany(s => Events(s, owner, dedicated)).OrderBy(e => e.At), DateTimeOffset.UtcNow, ct);
                    await push.DeliverPendingAsync(DateTimeOffset.UtcNow, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            // Do not log credential files, registration tokens or provider response bodies.
            catch (Exception e) { logger.LogWarning("Mobile signal notification cycle failed ({FailureType})", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}

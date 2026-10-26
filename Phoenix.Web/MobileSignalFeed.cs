using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Phoenix.Web;

public sealed record DeviceSignalAlert(long Sequence,string Id,string Kind,DateTime At,string Symbol,string Direction,string? ExitKind);
public sealed record DeviceSignalPage(long Cursor,string Account,IReadOnlyList<DeviceSignalAlert> Events,DateTime ServerTimeUtc);

// First-party authenticated feed. No Telegram, Firebase, third-party account or device secret is needed.
public sealed class MobileSignalFeed(ServerOrderStore store,DedicatedTelegramOptions dedicated)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private DateTime refreshed=DateTime.MinValue;
    private readonly string path=(Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")??Path.Combine(AppContext.BaseDirectory,"data","server-signals.json"))+".device-alerts.db";
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString());await db.OpenAsync(token);
        using var cmd=db.CreateCommand();cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS device_signal_events(seq INTEGER PRIMARY KEY AUTOINCREMENT,owner TEXT NOT NULL,event_id TEXT NOT NULL,payload TEXT NOT NULL,at_utc TEXT NOT NULL,UNIQUE(owner,event_id));
            CREATE INDEX IF NOT EXISTS device_signal_owner_seq ON device_signal_events(owner,seq);
            """;await cmd.ExecuteNonQueryAsync(token);return db;
    }
    public async Task CaptureAsync(string owner,CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if(DateTime.UtcNow-refreshed<TimeSpan.FromSeconds(1))return;
            var now=DateTime.UtcNow;
            var signals=await store.NotificationSignalsAsync(now.AddDays(-1),token);
            await using var db=await OpenAsync(token);using var tx=db.BeginTransaction();
            foreach(var pair in signals.SelectMany(s=>MobileSignalWorker.Events(s,owner,dedicated).Select(e=>(Signal:s,Event:e))).OrderBy(p=>p.Event.At))
            {
                var e=pair.Event;var s=pair.Signal;
                if(e.At.Kind!=DateTimeKind.Utc || e.At<now.AddDays(-1) || e.At>now)continue;
                using var cmd=db.CreateCommand();cmd.Transaction=tx;
                var payload=new DeviceSignalAlert(0,e.Id,e.Kind,e.At,s.Symbol,s.Direction,s.RiskFreeCloseKind);
                cmd.CommandText="INSERT OR IGNORE INTO device_signal_events(owner,event_id,payload,at_utc) VALUES($owner,$id,$payload,$at)";
                cmd.Parameters.AddWithValue("$owner",owner.ToLowerInvariant());cmd.Parameters.AddWithValue("$id",e.Id);
                cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(payload));cmd.Parameters.AddWithValue("$at",e.At.ToString("O"));
                await cmd.ExecuteNonQueryAsync(token);
            }
            using var clean=db.CreateCommand();clean.Transaction=tx;clean.CommandText="DELETE FROM device_signal_events WHERE at_utc<$before";
            clean.Parameters.AddWithValue("$before",now.AddDays(-7).ToString("O"));await clean.ExecuteNonQueryAsync(token);
            using var filtered=db.CreateCommand();filtered.Transaction=tx;
            filtered.CommandText="""
                DELETE FROM device_signal_events WHERE json_extract(payload,'$.Kind') IN ('Created','Cancelled','ManualClosed')
                OR (json_extract(payload,'$.Kind')='Expired' AND NOT EXISTS(
                    SELECT 1 FROM device_signal_events near WHERE near.owner=device_signal_events.owner
                    AND near.event_id=replace(device_signal_events.event_id,':Expired',':NearEntry')));
                """;
            await filtered.ExecuteNonQueryAsync(token);
            tx.Commit();refreshed=now;
        }
        finally{gate.Release();}
    }
    public async Task<DeviceSignalPage> ReadAsync(string owner,long? after,CancellationToken token)
    {
        await CaptureAsync(owner,token);await gate.WaitAsync(token);
        try
        {
            await using var db=await OpenAsync(token);using var cmd=db.CreateCommand();
            cmd.Parameters.AddWithValue("$owner",owner.ToLowerInvariant());
            if(after is null)
            {
                cmd.CommandText="SELECT COALESCE(MAX(seq),0) FROM device_signal_events WHERE owner=$owner";
                return new(Convert.ToInt64(await cmd.ExecuteScalarAsync(token)),owner,[],DateTime.UtcNow);
            }
            if(after<0)throw new ArgumentException("نشانگر اعلان معتبر نیست.");
            cmd.CommandText="SELECT seq,payload FROM device_signal_events WHERE owner=$owner AND seq>$after ORDER BY seq LIMIT 100";
            cmd.Parameters.AddWithValue("$after",after.Value);var events=new List<DeviceSignalAlert>();
            using var reader=await cmd.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
                if(JsonSerializer.Deserialize<DeviceSignalAlert>(reader.GetString(1)) is { } alert)events.Add(alert with{Sequence=reader.GetInt64(0)});
            return new(events.Count==0?after.Value:events[^1].Sequence,owner,events,DateTime.UtcNow);
        }
        finally{gate.Release();}
    }
}

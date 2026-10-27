using System.Text.Json;
namespace Phoenix.Web;
public sealed partial class MobileSignalFeed
{
    public async Task<IReadOnlyList<WalletRecoveryCandidate>> WalletRecoveryCandidatesAsync(string owner,CancellationToken ct)
    {
        await HistoryAsync(owner,null,null,ct,"results");
        var signals=(await store.NotificationSignalsAsync(DateTime.MinValue,ct)).ToDictionary(s=>s.Id);
        // Recover journal rows from actual archived lifecycle facts as well as from the current queue.
        await gate.WaitAsync(ct);
        try
        {
            await using var db=await OpenAsync(ct);using var tx=db.BeginTransaction();
            foreach(var pair in signals.Values.SelectMany(s=>MobileSignalWorker.Events(s,owner,dedicated).Select(e=>(s,e)))
                .Where(p=>(p.e.Kind is "Target" or "RiskFree" or "StopLoss") && p.e.At.Kind==DateTimeKind.Utc&&p.e.At<=DateTime.UtcNow)
                .OrderByDescending(p=>p.e.At).Take(50))
            {
                using var seed=db.CreateCommand();seed.Transaction=tx;seed.CommandText="INSERT OR IGNORE INTO device_signal_history(owner,event_id,payload,at_utc) VALUES($o,$id,$p,$at)";
                var e=pair.e;var s=pair.s;var payload=new DeviceSignalAlert(0,e.Id,e.Kind,e.At,s.Symbol,s.Direction,s.RiskFreeCloseKind,s.EntryPrice,s.TakeProfit,s.StopLoss,s.Timeframe);
                seed.Parameters.AddWithValue("$o",owner.ToLowerInvariant());seed.Parameters.AddWithValue("$id",e.Id);seed.Parameters.AddWithValue("$p",JsonSerializer.Serialize(payload));seed.Parameters.AddWithValue("$at",e.At.ToString("O"));await seed.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        finally{gate.Release();}
        var page=await HistoryAsync(owner,null,null,ct,"results");
        return page.Events.Select(e=>new WalletRecoveryCandidate(e,Guid.TryParse(e.Id.Split(':')[0],out var id)&&signals.TryGetValue(id,out var s)?s:null)).ToArray();
    }
    public async Task<bool> SaveRecoveredWalletAsync(string owner,string eventId,decimal balance,DateTime at,string source,string reference,string transaction,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db=await OpenAsync(ct);using var tx=db.BeginTransaction();
            using var query=db.CreateCommand();query.Transaction=tx;query.CommandText="SELECT payload FROM device_signal_history WHERE owner=$o AND event_id=$id";
            query.Parameters.AddWithValue("$o",owner.ToLowerInvariant());query.Parameters.AddWithValue("$id",eventId);
            var raw=await query.ExecuteScalarAsync(ct) as string;
            if(raw is null||JsonSerializer.Deserialize<DeviceSignalAlert>(raw) is not {} e||e.WalletBalanceUsdt is not null||e.Kind is not ("Target" or "RiskFree" or "StopLoss"))return false;
            using var audit=db.CreateCommand();audit.Transaction=tx;audit.CommandText="CREATE TABLE IF NOT EXISTS device_wallet_recovery(owner TEXT NOT NULL,event_id TEXT NOT NULL,source TEXT NOT NULL,reference TEXT NOT NULL,transaction_id TEXT NOT NULL,balance TEXT NOT NULL,balance_at TEXT NOT NULL,recovered_at TEXT NOT NULL,PRIMARY KEY(owner,event_id));";
            await audit.ExecuteNonQueryAsync(ct);
            audit.CommandText="INSERT INTO device_wallet_recovery VALUES($o,$id,$src,$ref,$tid,$balance,$at,$now)";
            audit.Parameters.AddWithValue("$o",owner.ToLowerInvariant());audit.Parameters.AddWithValue("$id",eventId);audit.Parameters.AddWithValue("$src",source);audit.Parameters.AddWithValue("$ref",reference);audit.Parameters.AddWithValue("$tid",transaction);
            audit.Parameters.AddWithValue("$balance",balance.ToString(System.Globalization.CultureInfo.InvariantCulture));audit.Parameters.AddWithValue("$at",at.ToString("O"));audit.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));await audit.ExecuteNonQueryAsync(ct);
            using var update=db.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE device_signal_history SET payload=$p WHERE owner=$o AND event_id=$id";
            update.Parameters.AddWithValue("$p",JsonSerializer.Serialize(e with{WalletBalanceUsdt=balance,WalletAtUtc=at,WalletSource=source}));update.Parameters.AddWithValue("$o",owner.ToLowerInvariant());update.Parameters.AddWithValue("$id",eventId);await update.ExecuteNonQueryAsync(ct);tx.Commit();return true;
        }
        finally{gate.Release();}
    }
}

using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace Phoenix.Web;
public sealed record DeviceHistoryPage(IReadOnlyList<DeviceSignalAlert> Events,long? Before,bool HasMore,string? Focus,bool FocusFound);
public sealed partial class MobileSignalFeed
{
    private static async Task SaveHistoryAsync(SqliteConnection db,SqliteTransaction tx,string owner,ServerSignal s,MobileSignalEvent e,CancellationToken ct)
    {
        var payload=new DeviceSignalAlert(0,e.Id,e.Kind,e.At,s.Symbol,s.Direction,s.RiskFreeCloseKind,s.EntryPrice,s.TakeProfit,s.StopLoss,s.Timeframe,e.Kind is "Target" or "RiskFree" or "StopLoss" or "ManualClosed" ? s.ResultWalletBalanceUsdt : null,s.ResultWalletAtUtc,s.ResultWalletBalanceUsdt.HasValue?"LocalSnapshot":null,s.Origin,EventReason(s));
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO device_signal_history(owner,event_id,payload,at_utc) VALUES($o,$id,$p,$at) ON CONFLICT(owner,event_id) DO UPDATE SET payload=excluded.payload WHERE json_extract(device_signal_history.payload,'$.WalletBalanceUsdt') IS NULL AND json_extract(excluded.payload,'$.WalletBalanceUsdt') IS NOT NULL";
        cmd.Parameters.AddWithValue("$o",owner.ToLowerInvariant());cmd.Parameters.AddWithValue("$id",e.Id);
        cmd.Parameters.AddWithValue("$p",JsonSerializer.Serialize(payload));cmd.Parameters.AddWithValue("$at",e.At.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        using var origin=db.CreateCommand();origin.Transaction=tx;
        origin.CommandText="UPDATE device_signal_history SET payload=json_set(payload,'$.Origin',$origin) WHERE owner=$o AND event_id=$id AND json_extract(payload,'$.Origin') IS NOT $origin";
        origin.Parameters.AddWithValue("$o",owner.ToLowerInvariant());origin.Parameters.AddWithValue("$id",e.Id);origin.Parameters.AddWithValue("$origin",(object?)s.Origin??DBNull.Value);await origin.ExecuteNonQueryAsync(ct);
    }
    public async Task<DeviceHistoryPage> HistoryAsync(string owner,long? before,string? eventId,CancellationToken ct,string? view=null,string? source=null)
    {
        if(view is not (null or "all" or "results"))throw new ArgumentException("سربرگ معتبر نیست.");
        if(source is not (null or "normal" or "manual"))throw new ArgumentException("منشأ سیگنال معتبر نیست.");
        var sourceFilter=source=="manual" ? " AND json_extract(payload,'$.Origin')='ManualChart'" : source=="normal" ? " AND COALESCE(json_extract(payload,'$.Origin'),'')!='ManualChart'" : "";
        var resultFilter=view=="results" ? " AND json_extract(payload,'$.Kind') IN ('Target','RiskFree','StopLoss','ManualClosed','EntryError')" : "";
        if (view=="results" && includeInitialExpiry)
            resultFilter=" AND json_extract(payload,'$.Kind') IN ('Target','RiskFree','StopLoss','ManualClosed','EntryError','Expired')";
        resultFilter+=sourceFilter;
        if(before is <=0 || eventId?.Length>120)throw new ArgumentException("نشانگر رویداد معتبر نیست.");
        await CaptureAsync(owner,ct);await gate.WaitAsync(ct);
        try{
            await using var db=await OpenAsync(ct);
            using(var check=db.CreateCommand()){
                check.CommandText="SELECT COUNT(*) FROM device_history_initialized WHERE owner=$o";check.Parameters.AddWithValue("$o",owner.ToLowerInvariant());
                if(Convert.ToInt64(await check.ExecuteScalarAsync(ct))==0){
                    var signals=await store.NotificationSignalsAsync(DateTime.MinValue,ct);
                    using var tx=db.BeginTransaction();
                    foreach(var pair in signals.SelectMany(s=>MobileSignalWorker.Events(s,owner,dedicated,includeInitialExpiry,includeUnowned).Select(e=>(s,e))).OrderBy(p=>p.e.At))
                        if(pair.e.At.Kind==DateTimeKind.Utc && pair.e.At<=DateTime.UtcNow)await SaveHistoryAsync(db,tx,owner,pair.s,pair.e,ct);
                    using var migrate=db.CreateCommand();migrate.Transaction=tx;
                    migrate.CommandText="INSERT OR IGNORE INTO device_signal_history(owner,event_id,payload,at_utc) SELECT owner,event_id,payload,at_utc FROM device_signal_events WHERE owner=$o; INSERT OR IGNORE INTO device_history_initialized VALUES($o)";
                    migrate.Parameters.AddWithValue("$o",owner.ToLowerInvariant());await migrate.ExecuteNonQueryAsync(ct);tx.Commit();
                }
            }
            // Enrich pre-source history once without replacing wallet recovery metadata or replaying alerts.
            using(var check=db.CreateCommand()){
                check.CommandText="SELECT COUNT(*) FROM device_history_sources_initialized WHERE owner=$o";check.Parameters.AddWithValue("$o",owner.ToLowerInvariant());
                if(Convert.ToInt64(await check.ExecuteScalarAsync(ct))==0){
                    var manual=(await store.NotificationSignalsAsync(DateTime.MinValue,ct)).Where(s=>s.Origin==ManualSignals.Origin);
                    using var tx=db.BeginTransaction();
                    foreach(var signal in manual){using var update=db.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE device_signal_history SET payload=json_set(payload,'$.Origin','ManualChart') WHERE owner=$o AND event_id LIKE $id";update.Parameters.AddWithValue("$o",owner.ToLowerInvariant());update.Parameters.AddWithValue("$id",signal.Id+":%");await update.ExecuteNonQueryAsync(ct);}
                    using var done=db.CreateCommand();done.Transaction=tx;done.CommandText="INSERT OR IGNORE INTO device_history_sources_initialized VALUES($o)";done.Parameters.AddWithValue("$o",owner.ToLowerInvariant());await done.ExecuteNonQueryAsync(ct);tx.Commit();
                }
            }
            string? anchor=null;long anchorSeq=0;bool found=eventId is null;
            if(before.HasValue || eventId is not null){
                using var lookup=db.CreateCommand();lookup.CommandText="SELECT seq,at_utc FROM device_signal_history WHERE owner=$o AND "+(before.HasValue?"seq=$key":"event_id=$key")+resultFilter;
                lookup.Parameters.AddWithValue("$o",owner.ToLowerInvariant());lookup.Parameters.AddWithValue("$key",before.HasValue?(object)before.Value:eventId!);
                using var r=await lookup.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct)){anchorSeq=r.GetInt64(0);anchor=r.GetString(1);found=true;}
                else if(before.HasValue)throw new ArgumentException("این صفحهٔ تاریخچه در دسترس نیست.");
            }
            using var cmd=db.CreateCommand();cmd.Parameters.AddWithValue("$o",owner.ToLowerInvariant());
            cmd.CommandText="SELECT seq,payload FROM device_signal_history WHERE owner=$o"+resultFilter;
            if(anchor is not null){cmd.CommandText+=" AND (at_utc<$at OR (at_utc=$at AND seq"+(before.HasValue?"<":"<=")+"$seq))";cmd.Parameters.AddWithValue("$at",anchor);cmd.Parameters.AddWithValue("$seq",anchorSeq);}
            cmd.CommandText+=" ORDER BY at_utc DESC,seq DESC LIMIT 51";
            var rows=new List<DeviceSignalAlert>();using var reader=await cmd.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))if(JsonSerializer.Deserialize<DeviceSignalAlert>(reader.GetString(1)) is {} e)rows.Add(e with{Sequence=reader.GetInt64(0)});
            var more=rows.Count>50;if(more)rows.RemoveAt(50);var next=rows.LastOrDefault()?.Sequence;rows.Reverse();
            return new(rows,next,more,eventId,found);
        }finally{gate.Release();}
    }
}

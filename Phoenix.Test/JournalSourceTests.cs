using Phoenix.Web;
using Microsoft.Data.Sqlite;
internal static class JournalSourceTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    public static void Isolation()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var folder=Path.Combine(Path.GetTempPath(),"journal-source-"+Guid.NewGuid());Directory.CreateDirectory(folder);
        var old=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");var path=Path.Combine(folder,"queue.json");Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",path);
        try
        {
            var store=new ServerOrderStore(path,Path.Combine(folder,"archive.db"));Guid manualId=Guid.Empty;
            var at=DateTime.UtcNow.AddHours(-5);
            for(var i=0;i<64;i++){
                var isManual=i>=3;var s=new ServerSignal{Id=Guid.NewGuid(),Symbol=isManual?"ETHUSDT":"BTCUSDT",Direction="Long",RequestedByUsername="owner",Origin=isManual?ManualSignals.Origin:null,Status="Completed",CreatedAtUtc=at.AddMinutes(i),FilledAtUtc=at.AddMinutes(i),CompletedAtUtc=at.AddMinutes(i).AddSeconds(10),Outcome="Target",EntryPrice=100,TakeProfit=110,StopLoss=90,ResultWalletBalanceUsdt=25};
                if(i==3)manualId=s.Id;await store.AddAsync(s);
            }
            var feed=new MobileSignalFeed(store,new DedicatedTelegramOptions("fixture",null,null));
            var normal=await feed.HistoryAsync("owner",null,null,default,"results","normal");Check(normal.Events.Count==3&&!normal.HasMore&&normal.Events.All(e=>e.Origin is null),"Normal legacy results remain separate before limiting");
            var manual=await feed.HistoryAsync("owner",null,null,default,"results","manual");Check(manual.Events.Count==50&&manual.HasMore&&manual.Events.All(e=>e.Origin==ManualSignals.Origin),"Manual filtering precedes pagination");
            var older=await feed.HistoryAsync("owner",manual.Before,null,default,"results","manual");Check(older.Events.Count==11&&!older.HasMore&&!older.Events.Select(x=>x.Id).Intersect(manual.Events.Select(x=>x.Id)).Any(),"Manual result cursors do not overlap");
            var wrong=await feed.HistoryAsync("owner",null,manualId+":Target",default,"results","normal");Check(!wrong.FocusFound,"Normal deep links cannot leak manual results");
            var own=await feed.HistoryAsync("owner",null,manualId+":Target",default,"results","manual");Check(own.FocusFound,"Manual deep link remains resolvable");
            var isolated=await feed.HistoryAsync("other",null,manualId+":Target",default,"results","manual");Check(!isolated.FocusFound&&!isolated.Events.Any(),"Source filters preserve owner isolation");
            var history=await store.GetHistoryAsync(30,1,default,"normal");Check(history.Count==1&&history[0].Signal.Symbol=="BTCUSDT","Main history filters source before row limit");
            // Simulate history produced before Origin was added, including recovered wallet metadata.
            await using(var db=new SqliteConnection("Data Source="+path+".device-alerts.db")){
                await db.OpenAsync();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE device_signal_history SET payload=json_set(json_remove(payload,'$.Origin'),'$.WalletSource','BybitTransaction','$.WalletBalanceUsdt',42) WHERE event_id=$id;DELETE FROM device_history_sources_initialized WHERE owner='owner'";cmd.Parameters.AddWithValue("$id",manualId+":Target");await cmd.ExecuteNonQueryAsync();
            }
            var restored=await new MobileSignalFeed(store,new DedicatedTelegramOptions("fixture",null,null)).HistoryAsync("owner",null,manualId+":Target",default,"results","manual");
            var target=restored.Events.Single(x=>x.Id==manualId+":Target");Check(target.Origin==ManualSignals.Origin&&target.WalletBalanceUsdt==42&&target.WalletSource=="BybitTransaction","Migration enriches source without discarding wallet recovery");
        }
        finally{Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",old);SignalHistoryStore.ClearConnectionPools();Directory.Delete(folder,true);}
    }
}

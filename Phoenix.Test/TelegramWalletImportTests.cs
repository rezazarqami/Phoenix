using Phoenix.Web;
using System.Text.Json;
internal static class TelegramWalletImportTests
{
    public static void Import()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var folder=Path.Combine(Path.GetTempPath(),"wallet-import-"+Guid.NewGuid());Directory.CreateDirectory(folder);
        var old=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");var path=Path.Combine(folder,"queue.json");Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",path);
        try
        {
            var store=new ServerOrderStore(path,Path.Combine(folder,"archive.db"));var options=new DedicatedTelegramOptions("dedicated",null,null);var now=DateTime.UtcNow.AddDays(-10);
            var signals=new List<ServerSignal>();
            for(var i=0;i<52;i++)
            {
                var at=now.AddMinutes(i);
                var s=new ServerSignal{Id=Guid.NewGuid(),Symbol="BTCUSDT",Direction="Long",RequestedByUsername="owner",CreatedAtUtc=at,FilledAtUtc=at,CompletedAtUtc=at,TargetReachedAtUtc=at,Outcome="Target",Status="Completed",EntryPrice=100+i,TakeProfit=200+i,StopLoss=90+i};
                await store.AddAsync(s);signals.Add(s);
            }
            // The export must be able to enrich removed archive records too.
            await store.RemoveAsync(signals[49].Id);
            object Message(ServerSignal s,decimal balance,int id,bool rich=false)=>new{id,date_unixtime=new DateTimeOffset(s.CompletedAtUtc!.Value.AddSeconds(2)).ToUnixTimeSeconds().ToString(),text=rich?(object)new object[]{new{type="bold",text="🏆 قیمت به تارگت رسید"},$"\nنماد: {s.Symbol}\nجهت: Long\nورود: {s.EntryPrice}\nتارگت: {s.TakeProfit}\nاستاپ: {s.StopLoss}\n💰 موجودی کیف پول USDT در زمان اعلان: {balance} USDT"}:$"🏆 قیمت به تارگت رسید\nنماد: {s.Symbol}\nجهت: Long\nورود: {s.EntryPrice}\nتارگت: {s.TakeProfit}\nاستاپ: {s.StopLoss}\n💰 موجودی کیف پول USDT در زمان اعلان: {balance} USDT"};
            var export=JsonSerializer.SerializeToElement(new{messages=new[]{Message(signals[0],999,1),Message(signals[49],0,2,true),Message(signals[50],12.34m,3),Message(signals[50],50,4),Message(signals[51],25.5m,5)}});
            var feed=new MobileSignalFeed(store,options);
            var denied=await feed.ImportWalletsAsync("other",export,default);Check(denied.Imported==0,"Imports cannot cross owner boundaries");
            var result=await feed.ImportWalletsAsync("owner",export,default);Check(result.Imported==2&&result.Examined==50,"Only recent fifty results with unambiguous messages may be imported");
            var page=await feed.HistoryAsync("owner",null,null,default,"results");
            Check(page.Events.Single(e=>e.Id==signals[49].Id+":Target").WalletBalanceUsdt==0,"A zero wallet and rich Telegram entities must be retained");
            Check(page.Events.Single(e=>e.Id==signals[50].Id+":Target").WalletBalanceUsdt is null,"Conflicting Telegram balances must not be guessed");
            Check(page.Events.Single(e=>e.Id==signals[51].Id+":Target").WalletBalanceUsdt==25.5m,"Wallet must match exact trade levels and time");
            var restart=new MobileSignalFeed(store,options);var repeat=await restart.ImportWalletsAsync("owner",JsonSerializer.SerializeToElement(new{messages=new[]{Message(signals[51],888,6)}}),default);
            Check(repeat.Imported==0,"Import must not overwrite recorded balances, even after restart");
            Check((await restart.HistoryAsync("owner",null,null,default,"results")).Events.Single(e=>e.Id==signals[51].Id+":Target").WalletBalanceUsdt==25.5m,"Persisted source balance must remain unchanged");
        }
        finally{Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",old);SignalHistoryStore.ClearConnectionPools();Directory.Delete(folder,true);}
    }
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}

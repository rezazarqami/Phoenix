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
                if(i==47){s.Outcome="StopLoss";s.TargetReachedAtUtc=null;s.StopLossReachedAtUtc=at;}
                if(i==48){s.Outcome="RiskFree";s.TargetReachedAtUtc=null;s.RiskFreeClosedAtUtc=at;}
                await store.AddAsync(s);signals.Add(s);
            }
            // The export must be able to enrich removed archive records too.
            await store.RemoveAsync(signals[49].Id);
            string Heading(ServerSignal s)=>s.Outcome=="RiskFree"?"💚 معامله با پلهٔ دوم ریسک‌فری بسته شد":s.Outcome=="StopLoss"?"🛑 قیمت به سطح استاپ‌لاس رسید":"🏆 قیمت به تارگت رسید";
            object Message(ServerSignal s,decimal balance,int id,bool rich=false)=>new{id,date_unixtime=new DateTimeOffset(s.CompletedAtUtc!.Value.AddSeconds(2)).ToUnixTimeSeconds().ToString(),text=rich?(object)new object[]{new{type="bold",text=Heading(s)},$"\nنماد: {s.Symbol}\nجهت: Long\nورود: {s.EntryPrice}\nتارگت: {s.TakeProfit}\nاستاپ: {s.StopLoss}\n💰 موجودی کیف پول USDT در زمان اعلان: {balance} USDT"}:$"{Heading(s)}\nنماد: {s.Symbol}\nجهت: Long\nورود: {s.EntryPrice}\nتارگت: {s.TakeProfit}\nاستاپ: {s.StopLoss}\n💰 موجودی کیف پول USDT در زمان اعلان: {balance} USDT"};
            var export=JsonSerializer.SerializeToElement(new{messages=new[]{Message(signals[47],18.4m,7),Message(signals[48],22.3m,8),Message(signals[0],999,1),Message(signals[49],0,2,true),Message(signals[50],12.34m,3),Message(signals[50],50,4),Message(signals[51],25.5m,5)}});
            var feed=new MobileSignalFeed(store,options);
            var denied=await feed.ImportWalletsAsync("other",export,default);Check(denied.Imported==0,"Imports cannot cross owner boundaries");
            var result=await feed.ImportWalletsAsync("owner",export,default);Check(result.Imported==4&&result.Examined==50,"Only recent fifty results with unambiguous messages may be imported");
            var page=await feed.HistoryAsync("owner",null,null,default,"results");
            Check(page.Events.Single(e=>e.Id==signals[47].Id+":StopLoss").WalletBalanceUsdt==18.4m&&page.Events.Single(e=>e.Id==signals[48].Id+":RiskFree").WalletBalanceUsdt==22.3m,"Stop-loss and risk-free close messages must import as final results");
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

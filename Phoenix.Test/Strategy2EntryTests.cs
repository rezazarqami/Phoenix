using Phoenix.Web;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Strategy2EntryTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    public static void EntryAndExpiry()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var root=Path.Combine(Path.GetTempPath(),"strategy2-entry-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            foreach(var direction in new[]{"Long","Short"})
            {
                var options=new Strategy2Options(null,null,Path.Combine(root,direction+".json"),Path.Combine(root,direction+".db"),true,.995m);
                var runtime=new Strategy2Runtime(options);
                var notifier=new Strategy2TelegramNotifier(new(null,null),NullLogger<Strategy2TelegramNotifier>.Instance,runtime);
                var worker=new Strategy2Worker(runtime,notifier,NullLogger<Strategy2Worker>.Instance);
                ServerSignal Signal(string symbol)=>new(){Id=Guid.NewGuid(),Symbol=symbol,Direction=direction,Status="Pending",EntryPrice=100,TakeProfit=direction=="Long"?110:90,Floor=80,Ceiling=120,CreatedAtUtc=DateTime.UtcNow,RequestedByUsername="owner",ExpireStage="Initial"};
                var signal=Signal("BTCUSDT");await runtime.Store.AddAsync(signal);
                await worker.ObservePriceAsync(signal.Id,direction=="Long"?102:98,true,default);
                Check((await runtime.Store.GetAllAsync()).Single().ExpireStage=="Target","Expiry activation is preserved");
                var touch = worker.ObservePriceAsync(signal.Id,100,false,default);
                var competingExpiry = worker.ObservePriceAsync(signal.Id,direction=="Long"?110:90,true,default);
                await Task.WhenAll(touch,competingExpiry);
                var claimed=(await runtime.Store.GetAllAsync()).Single();
                Check(claimed.Status=="Submitting"&&claimed.EntryTriggeredAtUtc.HasValue,"Live touch is persisted without waiting for any exchange or Telegram request");
                await worker.ObservePriceAsync(signal.Id,direction=="Long"?110:90,true,default);
                claimed=(await runtime.Store.GetAllAsync()).Single();
                Check(claimed.Status=="Submitting"&&claimed.CompletedAtUtc is null,"Later polling expiry cannot overwrite a claimed entry");
                var busy=Signal("ETHUSDT");await runtime.Store.AddAsync(busy);await worker.ObservePriceAsync(busy.Id,100,true,default);
                Check((await runtime.Store.GetAllAsync()).Single(s=>s.Id==busy.Id).ExpireReason=="PositionAlreadyOpen","Exclusive-position rule retained");
                var initial=Signal("INITIALUSDT");await runtime.Store.AddAsync(initial);
                await worker.ObservePriceAsync(initial.Id,direction=="Long"?130:70,true,default);
                Check((await runtime.Store.GetAllAsync()).Single(s=>s.Id==initial.Id).ExpireReason=="InitialBoundary","A genuine initial expiry remains valid");
                var returned=Signal("RETURNUSDT");await runtime.Store.AddAsync(returned);
                await worker.ObservePriceAsync(returned.Id,direction=="Long"?102:98,true,default);
                await worker.ObservePriceAsync(returned.Id,direction=="Long"?110:90,true,default);
                Check((await runtime.Store.GetAllAsync()).Single(s=>s.Id==returned.Id).ExpireReason=="TargetAfterActivation","Target expiry remains valid when entry was never observed");
                var paused=Signal("SOLUSDT");await runtime.Store.AddAsync(paused);var recovery=Signal("XRPUSDT");recovery.Status="RecoveryRequired";await runtime.Store.AddAsync(recovery);
                await worker.ObservePriceAsync(paused.Id,direction=="Long"?130:70,true,default);
                await worker.ObservePriceAsync(paused.Id,100,false,default);
                Check((await runtime.Store.GetAllAsync()).Single(s=>s.Id==paused.Id).Status=="Pending","Recovery pause cannot be silently converted into expiry");
            }
            var journalOptions=new Strategy2Options(null,null,Path.Combine(root,"journal.json"),Path.Combine(root,"journal.db"),true,.995m);
            var strategy=new Strategy2Runtime(journalOptions);
            var at=DateTime.UtcNow.AddHours(-1);
            await strategy.Store.AddAsync(new(){Id=Guid.NewGuid(),Symbol="S2USDT",Direction="Long",RequestedByUsername="owner",CreatedAtUtc=at,Status="Expired",Outcome="Expired",ExpireReason="InitialBoundary",ExpiredAtUtc=at.AddMinutes(1),CompletedAtUtc=at.AddMinutes(1)});
            var feed=strategy.Journal(new DedicatedTelegramOptions("fixture",null,null));
            var page=await feed.HistoryAsync("owner",null,null,default,"results");
            Check(page.Events.Count==1&&page.Events[0].Kind=="Expired"&&page.Events[0].Reason!.Contains("مرز اولیه"),"Strategy 2 results include initial expiry with actual reason");
            Check(!(await feed.HistoryAsync("other",null,null,default,"results")).Events.Any(),"Strategy 2 journal preserves owner isolation");
            await strategy.Store.AddAsync(new(){Id=Guid.NewGuid(),Symbol="LEGACYS2USDT",Direction="Short",CreatedAtUtc=at,Status="Completed",Outcome="Target",FilledAtUtc=at,TargetReachedAtUtc=at.AddMinutes(2),CompletedAtUtc=at.AddMinutes(2)});
            // This shared legacy feed is reachable only through the admin-only mobile history endpoint.
            var restartedFeed=new Strategy2Runtime(journalOptions).Journal(new DedicatedTelegramOptions("fixture",null,null));
            var legacy=await restartedFeed.HistoryAsync("owner",null,null,default,"results");
            Check(legacy.Events.Any(e=>e.Symbol=="LEGACYS2USDT"&&e.Kind=="Target"),"Pre-owner Strategy 2 history remains accessible to admins");
        }
        finally{SignalHistoryStore.ClearConnectionPools();Directory.Delete(root,true);}
    }
}

using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Reflection;

internal static class MobileFeedTests
{
    public static void DurablePrivateFeed()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var dir=Path.Combine(Path.GetTempPath(),"phoenix-feed-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        var before=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");var queue=Path.Combine(dir,"queue.json");Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",queue);
        try
        {
            var store=new ServerOrderStore(queue,Path.Combine(dir,"history.db"));var options=new DedicatedTelegramOptions("arman",null,null);
            MobileSignalFeed Feed()=>new(store,options);
            var bootstrap=await Feed().ReadAsync("owner",null,default);Check(bootstrap.Events.Count==0 && bootstrap.Cursor==0,"Fresh enrollment must bootstrap without replaying old notifications");
            var now=DateTime.UtcNow;
            var signal=new ServerSignal{Id=Guid.NewGuid(),Symbol="BTCUSDT",Direction="Long",RequestedByUsername="owner",Status="Completed",
                CreatedAtUtc=now.AddMinutes(-2),ExpireAdjustedAtUtc=now.AddMinutes(-1),EntryTriggeredAtUtc=now.AddSeconds(-50),FilledAtUtc=now.AddSeconds(-40),
                EarlyProtectionAtUtc=now.AddSeconds(-30),RiskFreeReachedAtUtc=now.AddSeconds(-20),CompletedAtUtc=now.AddSeconds(-10),Outcome="RiskFree",RiskFreeCloseKind="SecondStepLimit"};
            await store.AddAsync(signal);
            foreach(var (user,pub) in new[]{("other",(int?)null),("arman",(int?)1)})await store.AddAsync(new ServerSignal{Id=Guid.NewGuid(),Symbol="PRIVATEUSDT",Direction="Long",RequestedByUsername=user,PublicSignalNumber=pub,CreatedAtUtc=now});
            var page=await Feed().ReadAsync("owner",bootstrap.Cursor,default);
            Check(page.Events.Select(e=>e.Kind).SequenceEqual(new[]{"Created","NearEntry","Opened","FirstProtection","RiskFreeReached","RiskFree"}),"Feed must cover the full real lifecycle in timestamp order");
            Check(page.Events.All(e=>e.Symbol=="BTCUSDT"),"Private/dedicated signals must not leak into owner's feed");
            Check(page.Events.Last().ExitKind=="SecondStepLimit","Risk-free exit metadata must reach native clients");
            await store.RemoveAsync(signal.Id);
            Check((await Feed().ReadAsync("owner",page.Cursor,default)).Events.Count==0,"Restart/removal must not duplicate previously recorded events");
            var reopened=new ServerOrderStore(queue,Path.Combine(dir,"history.db"));var restarted=new MobileSignalFeed(reopened,options);
            var replay=await restarted.ReadAsync("owner",bootstrap.Cursor,default);
            Check(replay.Events.Select(e=>e.Sequence).SequenceEqual(page.Events.Select(e=>e.Sequence)),"Missed-event recovery must survive server and store restart");
            var fresh=await restarted.ReadAsync("owner",null,default);Check(fresh.Events.Count==0 && fresh.Cursor==page.Cursor,"New activation must skip historical alerts");
        }
        finally{Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",before);SignalHistoryStore.ClearConnectionPools();Directory.Delete(dir,true);}
    }
    public static void TerminalEventPrecedesTelegram()=>PersistFirst().GetAwaiter().GetResult();
    private static async Task PersistFirst()
    {
        var dir=Path.Combine(Path.GetTempPath(),"phoenix-before-telegram-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        var before=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");var queue=Path.Combine(dir,"queue.json");Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",queue);
        var release=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store=new ServerOrderStore(queue,Path.Combine(dir,"history.db"));
            var signal=new ServerSignal{Id=Guid.NewGuid(),Symbol="BTCUSDT",Direction="Long",RequestedByUsername="owner",Status="Filled",EntryPrice=100,TakeProfit=110,StopLoss=90,CreatedAtUtc=DateTime.UtcNow.AddMinutes(-1),FilledAtUtc=DateTime.UtcNow.AddSeconds(-30)};
            await store.AddAsync(signal);
            var client=new BybitDemoClient(new("fixture","fixture"),new HttpClient(new BlockWallet(async request=>{
                Check(request.RequestUri!.AbsolutePath.EndsWith("wallet-balance"),"Unexpected exchange call in persistence test");entered.TrySetResult();return await release.Task;
            })));
            var telegram=new TelegramNotifier(new(null,null),new(null,null),new TelegramAccessStore(),client,store,NullLogger<TelegramNotifier>.Instance);
            using var worker=new DemoOrderWorker(client,new(null,null),new ServerState(),store,telegram,NullLogger<DemoOrderWorker>.Instance);
            var tracking=(Task)typeof(DemoOrderWorker).GetMethod("TrackLevelsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(worker,[signal,110m,CancellationToken.None])!;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!tracking.IsCompleted,"Fixture must block the Telegram notification's wallet lookup");
            var feed=await new MobileSignalFeed(store,new("arman",null,null)).ReadAsync("owner",0,default);
            Check(feed.Events.Any(e=>e.Kind=="Target"),"Direct device alert must already see target before Telegram-side work finishes");
            release.TrySetResult(new(HttpStatusCode.ServiceUnavailable));await tracking;
        }
        finally{release.TrySetResult(new(HttpStatusCode.ServiceUnavailable));Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",before);SignalHistoryStore.ClearConnectionPools();Directory.Delete(dir,true);}
    }
    private sealed class BlockWallet(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request);}

    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}

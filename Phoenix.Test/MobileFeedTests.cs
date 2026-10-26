using Phoenix.Web;

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
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}

using Phoenix.Web;
internal static class TimeframeRiskTests
{
    public static void Limits()=>Verify().GetAwaiter().GetResult();
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    private static async Task Verify()
    {
        var path=Path.Combine(Path.GetTempPath(),"timeframe-"+Guid.NewGuid());Directory.CreateDirectory(path);
        var old=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",Path.Combine(path,"queue.json"));
        try{
            ServerOrderStore Open()=>new(Path.Combine(path,"queue.json"),Path.Combine(path,"history.db"));
            var store=Open();
            ServerSignal New(string tf="1")=>new(){Id=Guid.NewGuid(),Symbol="BTCUSDT",Direction="Long",Timeframe=tf,Status="Pending",EntryPrice=100,TakeProfit=110,StopLoss=90,RequestedByUsername="owner",CreatedAtUtc=DateTime.UtcNow};
            var signals=Enumerable.Range(0,12).Select(_=>New()).ToArray();foreach(var s in signals)await store.AddAsync(s);
            var results=await Task.WhenAll(signals.Select(s=>store.TryClaimSubmissionAsync(s.Id,100)));
            Check(results.Count(x=>x)==2,"Concurrent entry claims must reserve at most two default slots");
            var other=New("5");await store.AddAsync(other);Check(await store.TryClaimSubmissionAsync(other.Id,100),"Limits must be independent by timeframe");
            await store.ChangeTimeframeRuleAsync("1",3);var snapshot=await store.GetAllAsync();var third=signals.First(s=>snapshot.Single(x=>x.Id==s.Id).Status!="Submitting");
            Check(await store.TryClaimSubmissionAsync(third.Id,100),"Configured three-slot cap must allow a third reservation");
            var active=(await store.GetAllAsync()).Where(s=>s.Timeframe=="1"&&s.Status=="Submitting").ToArray();
            foreach(var s in active.Take(2)){s.FilledAtUtc=DateTime.UtcNow;s.Status="Completed";s.Outcome="StopLoss";s.CompletedAtUtc=DateTime.UtcNow;s.StopLossReachedAtUtc=s.CompletedAtUtc;await store.UpdateAsync(s);await store.UpdateAsync(s);}
            Check(!(await store.TimeframeRulesAsync()).Single(x=>x.Rule.Timeframe=="1").Rule.Blocked,"Two-stop brake applies to caps of two or fewer");
            await store.ChangeTimeframeRuleAsync("1",2);store=Open();var blocked=(await store.TimeframeRulesAsync()).Single(x=>x.Rule.Timeframe=="1");
            Check(blocked.Rule.Blocked&&blocked.Rule.ConsecutiveStops==2,"Brake, cap and idempotent stop streak must survive restart");
            var touched=(await store.GetAllAsync()).First(s=>s.Timeframe=="1"&&s.Status=="Pending");Check(!await store.TryClaimSubmissionAsync(touched.Id,99),"Blocked timeframe cannot submit");
            var expired=(await store.GetAllAsync()).Single(s=>s.Id==touched.Id);Check(expired.Outcome=="Expired"&&expired.ExpireReason=="TimeframeBlocked","Touches while blocked must expire permanently");
            // At release, current-price reconciliation expires ready entries before clearing the brake.
            var queuedTickSignal=New();queuedTickSignal.Symbol="ETHUSDT";await store.AddAsync(queuedTickSignal);var queuedTickAt=DateTime.UtcNow;
            await store.ResumeTimeframeAsync("1",new Dictionary<string,decimal>{{"BTCUSDT",99},{"ETHUSDT",110}});
            Check(!await store.TryClaimSubmissionAsync(queuedTickSignal.Id,99,default,queuedTickAt),"A touch queued before resume must expire rather than enter after release");
            Check(!(await store.TimeframeRulesAsync()).Single(x=>x.Rule.Timeframe=="1").Rule.Blocked,"Explicit resume clears only this brake");
            Check((await store.GetAllAsync()).Where(s=>s.Timeframe=="1"&&s.Id!=third.Id&&s.CompletedAtUtc is null).All(s=>s.Status!="Pending"),"Release must not resurrect blocked ready entries");
            Check(!await store.TryClaimSubmissionAsync(touched.Id,99),"Previously expired entry remains terminal after resume");
            var fresh=New();await store.AddAsync(fresh);Check(!await store.TryClaimSubmissionAsync(fresh.Id,100,default,queuedTickAt)&& (await store.GetAllAsync()).Single(s=>s.Id==fresh.Id).Status=="Pending","Ticks predating a new signal are ignored without expiring it");Check(await store.TryClaimSubmissionAsync(fresh.Id,100),"New entries may claim a free slot after explicit resume");
            var failed=New("15");await store.AddAsync(failed);await store.TryClaimSubmissionAsync(failed.Id,100);failed=(await store.GetAllAsync()).Single(s=>s.Id==failed.Id);failed.Status="Error";failed.Error="Bybit 110007: insufficient balance";await store.UpdateAsync(failed);
            var feed=new MobileSignalFeed(store,new DedicatedTelegramOptions("dedicated",null,null));var history=await feed.HistoryAsync("owner",null,null,default,view:"results");
            var current=new DeviceSignalPage(12,"owner",[new(10,"entry:EntryError","EntryError",DateTime.UtcNow,"BTCUSDT","Long",null),new(11,"manual:ManualClosed","ManualClosed",DateTime.UtcNow,"ETHUSDT","Short",null),new(12,"near:NearEntry","NearEntry",DateTime.UtcNow,"SOLUSDT","Long",null)],DateTime.UtcNow);
            Check(MobileSignalFeed.ForClient(current,1).Events.Count==1&&MobileSignalFeed.ForClient(current,1).Cursor==12&&MobileSignalFeed.ForClient(current,2).Events.Count==3,"Older Android clients skip unknown kinds without stalling the durable cursor");
            var error=history.Events.Single(e=>e.Id==failed.Id+":EntryError");Check(error.Reason?.Contains("موجودی")==true&&error.Direction=="Long","Entry failure result must identify balance reason and direction");
            var manual=New("60");manual.Status="Filled";manual.FilledAtUtc=DateTime.UtcNow;await store.AddAsync(manual);manual.Status="Completed";manual.Outcome="ManualClosed";manual.CompletedAtUtc=DateTime.UtcNow;await store.UpdateAsync(manual);
            Check(MobileSignalWorker.Events(manual,"owner",new("dedicated",null,null)).Any(e=>e.Kind=="ManualClosed"),"Confirmed manual closure must emit its own terminal notification");
            var untouched=New("240");await store.AddAsync(untouched);Check((await store.TimeframeRulesAsync()).Single(x=>x.Rule.Timeframe=="240").Rule.MaximumOpen==2,"Four-hour timeframe has the same default policy");
        }finally{Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",old);SignalHistoryStore.ClearConnectionPools();Directory.Delete(path,true);}
    }
}

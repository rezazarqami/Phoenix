using Phoenix.Web;
using System.Text.Json.Nodes;

internal static class TimeframeAlertTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    public static void DurableNotices()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var folder=Path.Combine(Path.GetTempPath(),"timeframe-alerts-"+Guid.NewGuid());Directory.CreateDirectory(folder);
        var previous=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");var queue=Path.Combine(folder,"queue.json");Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",queue);
        try
        {
            ServerOrderStore Open()=>new(queue,Path.Combine(folder,"history.db"));var store=Open();
            var options=new DedicatedTelegramOptions("dedicated",null,null);
            ServerSignal New(string symbol,string tf="1")=>new(){Id=Guid.NewGuid(),Symbol=symbol,Direction="Long",Timeframe=tf,Status="Filled",EntryPrice=100,TakeProfit=110,StopLoss=90,CreatedAtUtc=DateTime.UtcNow.AddMinutes(-1),FilledAtUtc=DateTime.UtcNow,RequestedByUsername="owner"};
            async Task End(ServerSignal signal,string outcome="StopLoss"){
                await store.AddAsync(signal);signal.Status="Completed";signal.Outcome=outcome;signal.CompletedAtUtc=DateTime.UtcNow;
                if(outcome=="StopLoss")signal.StopLossReachedAtUtc=signal.CompletedAtUtc;else signal.TargetReachedAtUtc=signal.CompletedAtUtc;
                await store.UpdateAsync(signal);await store.UpdateAsync(signal);
            }
            var first=New("BTCUSDT");await End(first);
            Check(!(await store.TimeframeLockNoticesAsync(DateTime.MinValue)).Any(),"One stop never emits a lock notice");
            // Upgrade a pre-notification risk file and keep the exact first symbol via queue/archive data.
            var riskFile=queue+".timeframe-risk.json";var legacy=JsonNode.Parse(await File.ReadAllTextAsync(riskFile))!;
            legacy.AsObject().Remove("stopStreaks");legacy.AsObject().Remove("lockNotices");await File.WriteAllTextAsync(riskFile,legacy.ToJsonString());
            await store.RemoveAsync(first.Id);store=Open();
            var second=New("ETHUSDT");await End(second);
            var notices=await store.TimeframeLockNoticesAsync(DateTime.MinValue);Check(notices.Count==1,"Exactly one notice per lock transition, despite repeated updates");
            var notice=notices.Single();Check(notice.Stops.Select(x=>x.Symbol).SequenceEqual(new[]{"BTCUSDT","ETHUSDT"}),"Lock includes both causal stop symbols, including archived legacy stop");
            var text=TimeframeAlertText.Locked(notice);Check(text.Reason!.Contains("۱ دقیقه")&&text.Reason.Contains("BTCUSDT")&&text.Reason.Contains("ETHUSDT"),"Lock text names timeframe and both stops");
            store=Open();Check((await store.TimeframeLockNoticesAsync(DateTime.MinValue)).Single().Id==notice.Id,"Stable lock ID survives server restart");
            var blocked=New("SOLUSDT");blocked.FilledAtUtc=null;blocked.Status="Pending";await store.AddAsync(blocked);
            Check(!await store.TryClaimSubmissionAsync(blocked.Id,100),"Trading block policy remains enforced");
            var saved=(await store.GetAllAsync()).Single(x=>x.Id==blocked.Id);var events=MobileSignalWorker.Events(saved,"owner",options).ToArray();
            Check(events.Length==1&&events[0].Kind=="EntryBlocked","Blocked entry emits one specific alert, not NearEntry plus generic expiry");
            Check(events[0].Reason!.Contains("SOLUSDT")&&events[0].Reason!.Contains("۱ دقیقه")&&events[0].Reason!.Contains("باز نشد"),"Blocked alert explains actual cause");
            MobileSignalFeed Feed()=>new(store,options);
            var page=await Feed().ReadAsync("owner",0,default);
            Check(page.Events.Count(x=>x.Kind=="TimeframeLocked")==1&&page.Events.Count(x=>x.Kind=="EntryBlocked")==1,"Direct phone feed contains both risk alerts");
            Check((await Feed().ReadAsync("owner",page.Cursor,default)).Events.Count==0,"Polling and restart never duplicate a delivered notice");
            var history=await Feed().HistoryAsync("owner",null,null,default);
            Check(history.Events.Any(x=>x.Id==text.Id&&x.Reason==text.Reason),"Lock explanation survives in history");
            var v2=MobileSignalFeed.ForClient(page,2);Check(v2.Events.Where(e=>e.Id==text.Id||e.Id==events[0].Id).All(e=>e.Kind=="EntryError"&&e.Reason!.Contains("تایم‌فریم")),"Installed v2 clients receive full reason through their existing supported kind");
            Check(MobileSignalFeed.ForClient(page,3).Events.Any(e=>e.Kind=="TimeframeLocked"),"New native client receives the precise event kind");
            await store.ResumeTimeframeAsync("1",new Dictionary<string,decimal>());
            await End(New("OLDUSDT"));await End(New("WINUSDT"),"Target");
            Check((await store.TimeframeLockNoticesAsync(DateTime.MinValue)).Count==1,"Winning result resets the existing stop streak");
            await End(New("ADAUSDT"));await End(New("XRPUSDT"));
            var again=(await store.TimeframeLockNoticesAsync(DateTime.MinValue)).Last();
            Check(again.Id!=notice.Id&&again.Stops.Select(x=>x.Symbol).SequenceEqual(new[]{"ADAUSDT","XRPUSDT"}),"Re-lock is a new event with only the new consecutive stops");
            var s2Options=new Strategy2Options(null,null,Path.Combine(folder,"s2.json"),Path.Combine(folder,"s2.db"),true,.995m);
            var s2=new Strategy2Runtime(s2Options);var s2Journal=await s2.Journal(options).ReadAsync("owner",0,default);
            Check(!s2Journal.Events.Any(),"Strategy 2 remains outside the main phone notification feed");
        }
        finally{Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",previous);SignalHistoryStore.ClearConnectionPools();Directory.Delete(folder,true);}
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;

internal static class NotificationReliabilityTests
{
    public static void RetryAndTerminalPersistence()=>Retry().GetAwaiter().GetResult();
    private static async Task Retry()
    {
        using var f=new Fixture();var s=f.Signal();await f.Store.AddAsync(s);
        var attempts=0;var delivered=new List<string>();
        var worker=f.Worker(payload=>{
            if(++attempts==1)return false;
            delivered.Add(payload.GetProperty("text").GetString()!);return true;
        });
        await worker.RunCycleAsync(f.Ledger);
        Check(delivered.Count==0,"Failed initial publication must remain pending");
        s.Status="Completed";s.FilledAtUtc=s.EntryTriggeredAtUtc!.Value.AddSeconds(1);
        s.CompletedAtUtc=s.FilledAtUtc.Value.AddSeconds(1);s.Outcome="Target";
        await f.Store.UpdateAsync(s);
        await worker.RunCycleAsync(f.Ledger);
        Check(delivered.Count==3 && delivered[0].Contains("Signal NO") && delivered[1].Contains("باز شد") && delivered[2].Contains("تارگت"),
            "Retry must publish even after completion, then deliver opened and target in order");
        var latest=(await f.Store.GetAllAsync()).Single();
        Check(latest.PublicTelegramMessageId>0 && latest.Outcome=="Target" && latest.Status=="Completed",
            "Delivery acknowledgment must persist on terminal state");
        // A real restart restores both the queue and the delivery ledger.
        var persisted=JsonSerializer.Deserialize<PublicSignalNotificationWorker.Ledger>(await File.ReadAllTextAsync(f.Store.NotificationLedgerPath))!;
        await worker.RunCycleAsync(persisted);
        Check(delivered.Count==3,"Acknowledged messages must not repeat after ledger reload");
    }

    public static void RaceAndStaleSnapshot()=>Race().GetAwaiter().GetResult();
    private static async Task Race()
    {
        using var f=new Fixture();var s=f.Signal();await f.Store.AddAsync(s);
        var stale=(await f.Store.GetAllAsync()).Single();var completed=false;
        var worker=f.Worker(_=>{
            if(!completed)
            {
                completed=true;
                s.Status="Completed";s.Outcome="StopLoss";s.CompletedAtUtc=DateTime.UtcNow;s.StopLossReachedAtUtc=s.CompletedAtUtc;
                f.Store.UpdateAsync(s).GetAwaiter().GetResult();
            }
            return true;
        });
        await worker.RunCycleAsync(f.Ledger);
        var current=(await f.Store.GetAllAsync()).Single();
        Check(current.CompletedAtUtc==s.CompletedAtUtc && current.Outcome=="StopLoss" && current.PublicTelegramMessageId>0,
            "Completion during Telegram request must preserve both outcome and acknowledgment");
        await f.Store.UpdateAsync(stale);
        Check((await f.Store.GetAllAsync()).Single().PublicTelegramMessageId>0,"Stale update cannot erase terminal delivery metadata");
        await worker.RunCycleAsync(f.Ledger);
        Check(f.Ledger.Sent.Any(k=>k.EndsWith(":StopLoss")),"Completion during publication must be picked up next cycle");
        // The same guard applies before completion, where UpdateAsync normally accepts writes.
        var active=f.Signal();await f.Store.AddAsync(active);await f.Store.SavePublicDeliveryAsync(active.Id,991,false);
        active.LastPrice=101;await f.Store.UpdateAsync(active);
        Check((await f.Store.GetAllAsync()).Single(x=>x.Id==active.Id).PublicTelegramMessageId==991,"Active stale trading writes must retain publication metadata");
    }

    public static void PerDestinationAndRemovedSignals()=>Routes().GetAwaiter().GetResult();
    private static async Task Routes()
    {
        using var f=new Fixture();var s=f.Signal();s.RequestedByUsername="arman";s.FilledAtUtc=DateTime.UtcNow;
        s.CompletedAtUtc=DateTime.UtcNow;s.Status="Completed";s.Outcome="Target";
        await f.Store.AddAsync(s);await f.Store.RemoveAsync(s.Id);
        var sent=new List<string>();var fail=true;
        var dedicated=new DedicatedTelegramOptions("arman","fake-dedicated","one,two",ChatStorePath:Path.Combine(f.Root,"no-chats.json"));
        var worker=f.Worker(p=>{
            var chat=p.GetProperty("chat_id").GetString()!;
            if(chat=="two" && fail)return false;
            sent.Add(chat+":"+p.GetProperty("text").GetString());return true;
        },dedicated);
        await worker.RunCycleAsync(f.Ledger);
        Check(sent.Count(x=>x.StartsWith("one:"))==3 && sent.All(x=>!x.StartsWith("two:")),"Each destination must own its root and event acknowledgments");
        fail=false;await worker.RunCycleAsync(f.Ledger);
        Check(sent.Count(x=>x.StartsWith("one:"))==3 && sent.Count(x=>x.StartsWith("two:"))==3,
            "A failed second chat must retry without duplicating successful first-chat delivery");
        Check((await f.Store.GetAllAsync()).Count==0,"Notification recovery must never resurrect removed orders");
        var archive=(await f.Store.GetHistoryAsync()).Single().Signal;
        Check(archive.Outcome=="Target" && archive.PublicSignalNumber>0,"Removed terminal signal must retain its publication number in history");
    }

    public static void OneFailureDoesNotBlockOtherSignals()=>Isolation().GetAwaiter().GetResult();
    private static async Task Isolation()
    {
        using var f=new Fixture();var a=f.Signal();a.Symbol="FAILUSDT";var b=f.Signal();b.Symbol="OKUSDT";
        await f.Store.AddAsync(a);await f.Store.AddAsync(b);
        var worker=f.Worker(p=>!p.GetProperty("text").GetString()!.Contains("FAIL/USDT"));
        await worker.RunCycleAsync(f.Ledger);
        var signals=await f.Store.GetAllAsync();
        Check(signals.Single(x=>x.Id==a.Id).PublicTelegramMessageId is null && signals.Single(x=>x.Id==b.Id).PublicTelegramMessageId>0,
            "Failure must be isolated to one signal; direct entry without near-price sample must still publish");
    }
    public static void PhotoFailureFallsBackToText()=>PhotoFallback().GetAwaiter().GetResult();
    private static async Task PhotoFallback()
    {
        using var f=new Fixture();var s=f.Signal();s.FilledAtUtc=DateTime.UtcNow;s.CompletedAtUtc=DateTime.UtcNow;
        s.Outcome="Target";s.Status="Completed";
        await f.Store.AddAsync(s,evidence:new SignalEvidence("15","Candles",[137,80,78,71]));
        var photos=0;var messages=0;
        var notifier=new PublicSignalNotifier(new("fake","channel"),NullLogger<PublicSignalNotifier>.Instance,
            new HttpClient(new Handler(request=>{
                if(request.RequestUri!.AbsolutePath.EndsWith("sendPhoto"))
                {photos++;return new(HttpStatusCode.BadRequest){Content=new StringContent("{\"ok\":false}")};}
                messages++;return new(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"result\":{\"message_id\":123}}")};
            })));
        await new PublicSignalNotificationWorker(f.Store,notifier,null!,NullLogger<PublicSignalNotificationWorker>.Instance).RunCycleAsync(f.Ledger);
        Check(photos==2 && messages==3,"Rejected photos must fall back to text for publication and outcome, retaining event order");
        var latest=(await f.Store.GetAllAsync()).Single();
        Check(latest.PublicTelegramMessageId==123 && latest.PublicReviewImageSentAtUtc is null,
            "Text fallback must not falsely acknowledge successful image delivery");
    }

    public static void FailedMarketSymbolDoesNotStarveOtherSignals()=>MarketIsolation().GetAwaiter().GetResult();
    private static async Task MarketIsolation()
    {
        using var f=new Fixture();var failed=f.Signal();failed.Symbol="FAILUSDT";failed.Status="Pending";failed.EntryTriggeredAtUtc=null;
        var healthy=f.Signal();healthy.Symbol="OKUSDT";healthy.Status="Pending";healthy.EntryTriggeredAtUtc=null;
        await f.Store.AddAsync(failed);await f.Store.AddAsync(healthy);
        var client=new BybitDemoClient(new(null,null),new HttpClient(new Handler(request=>{
            if(request.RequestUri!.Query.Contains("FAILUSDT"))return new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("unavailable")};
            var symbol=request.RequestUri.Query.Contains("OKUSDT")?"OKUSDT":"BTCUSDT";
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{retCode=0,result=new{list=new[]{new{symbol,lastPrice="110"}}}}))};
        })));
        using var worker=new DemoOrderWorker(client,new(null,null),new ServerState(),f.Store,null!,NullLogger<DemoOrderWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            var until=DateTime.UtcNow.AddSeconds(4);
            while(DateTime.UtcNow<until && (await f.Store.GetAllAsync()).Single(s=>s.Id==healthy.Id).LastPrice!=110m)
                await Task.Delay(20);
            Check((await f.Store.GetAllAsync()).Single(s=>s.Id==healthy.Id).LastPrice==110m,
                "A failing first market symbol must not prevent later signals being monitored");
        }
        finally{await worker.StopAsync(default);}
    }

    public static void RateLimitSurvivesRestart()=>RateLimit().GetAwaiter().GetResult();
    private static async Task RateLimit()
    {
        using var f=new Fixture();var s=f.Signal();await f.Store.AddAsync(s);
        var requests=0;
        var notifier=new PublicSignalNotifier(new("fake","channel"),NullLogger<PublicSignalNotifier>.Instance,
            new HttpClient(new Handler(_=>{
                requests++;
                return requests==1?new(HttpStatusCode.TooManyRequests){Content=new StringContent("{\"ok\":false,\"parameters\":{\"retry_after\":60}}")}
                    :new(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"result\":{\"message_id\":321}}")};
            })));
        var worker=new PublicSignalNotificationWorker(f.Store,notifier,null!,NullLogger<PublicSignalNotificationWorker>.Instance);
        await worker.RunCycleAsync(f.Ledger);
        var ledger=JsonSerializer.Deserialize<PublicSignalNotificationWorker.Ledger>(await File.ReadAllTextAsync(f.Store.NotificationLedgerPath))!;
        await worker.RunCycleAsync(ledger);
        Check(requests==1 && ledger.RetryAfterUtc.Values.Single()>DateTime.UtcNow.AddSeconds(50),
            "Telegram retry_after must be persisted and respected after restart");
        foreach(var key in ledger.RetryAfterUtc.Keys.ToArray())ledger.RetryAfterUtc[key]=DateTime.UtcNow.AddSeconds(-1);
        await worker.RunCycleAsync(ledger);
        Check(requests==2 && (await f.Store.GetAllAsync()).Single().PublicTelegramMessageId==321,
            "Delivery must resume after the Telegram cooldown");
    }

    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private sealed class Fixture:IDisposable
    {
        public readonly string Root=Path.Combine(Path.GetTempPath(),"phoenix-notification-test-"+Guid.NewGuid());
        public readonly ServerOrderStore Store;
        public readonly PublicSignalNotificationWorker.Ledger Ledger=new(){SinceUtc=DateTime.UtcNow.AddHours(-1)};
        public Fixture(){Directory.CreateDirectory(Root);Store=new(Path.Combine(Root,"queue.json"),Path.Combine(Root,"history.db"));}
        public ServerSignal Signal()=>new(){Id=Guid.NewGuid(),Symbol="TESTUSDT",Direction="Long",Timeframe="15",
            EntryPrice=100,TakeProfit=110,StopLoss=90,Ceiling=120,Floor=80,CreatedAtUtc=DateTime.UtcNow.AddMinutes(-5),
            EntryTriggeredAtUtc=DateTime.UtcNow.AddMinutes(-1),Status="Submitted"};
        public PublicSignalNotificationWorker Worker(Func<JsonElement,bool> send,DedicatedTelegramOptions? dedicated=null)
        {
            var sequence=100;
            var notifier=new PublicSignalNotifier(new("fake-public","channel"),dedicated??new("arman",null,null),
                NullLogger<PublicSignalNotifier>.Instance,new HttpClient(new Handler(request=>{
                    using var payload=JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    var ok=send(payload.RootElement);
                    if(payload.RootElement.TryGetProperty("reply_parameters",out var reply))
                        Check(reply.GetProperty("allow_sending_without_reply").GetBoolean(),"Deleted channel root must not permanently reject replies");
                    return new(ok?HttpStatusCode.OK:HttpStatusCode.ServiceUnavailable){Content=new StringContent(ok?
                        $"{{\"ok\":true,\"result\":{{\"message_id\":{++sequence}}}}}":"{\"ok\":false}")};
                })));
            return new(Store,notifier,null!,NullLogger<PublicSignalNotificationWorker>.Instance);
        }
        public void Dispose(){SignalHistoryStore.ClearConnectionPools();Directory.Delete(Root,true);}
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(send(request));}
}

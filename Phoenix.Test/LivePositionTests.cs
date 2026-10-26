using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Web;
using System.Net;

internal static class LivePositionTests
{
    public static void ExchangeValues() => Exchange().GetAwaiter().GetResult();
    private static async Task Exchange()
    {
        var calls = 0;
        var client = new BybitDemoClient(new("fixture", "fixture"), new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) throw new Exception("Position screen must be read-only");
            calls++;
            var body = calls == 1 ? """
                {"retCode":0,"result":{"list":[
                {"symbol":"BTCUSDT","side":"Buy","positionIdx":1,"size":"0.01","avgPrice":"100","markPrice":"103","unrealisedPnl":"1.25","positionIM":"5","leverage":"20","liqPrice":"80","takeProfit":"110","stopLoss":"99"},
                {"symbol":"EMPTY","side":"","positionIdx":0,"size":"0"}],"nextPageCursor":"next page"}}
                """ : """
                {"retCode":0,"result":{"list":[
                {"symbol":"ETHUSDT","side":"Sell","positionIdx":2,"size":"2","avgPrice":"200","markPrice":"210","unrealisedPnl":"-20","positionIM":"10","leverage":"5"},
                {"symbol":"PORTFOLIO","side":"Buy","positionIdx":0,"size":"1","unrealisedPnl":"0","positionIM":"","leverage":"","liqPrice":""}],"nextPageCursor":""}}
                """;
            if (calls == 2 && !request.RequestUri!.Query.Contains("cursor=next%20page")) throw new Exception("Cursor must be encoded");
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        })));
        var rows = await client.GetOpenPositionsAsync();
        Check(rows.Count == 3 && calls == 2, "All nonzero hedge positions across pages must be retained");
        Check(rows[0].UnrealisedPnl == 1.25m && rows[0].ReturnPercent == 25m && rows[0].MarkPrice == 103m, "Use exchange PnL, margin and mark, not simulated signal PnL");
        Check(rows[1].ReturnPercent == -200m && rows[1].PositionIndex == 2, "Loss and hedge side must be retained");
        Check(rows[2].ReturnPercent is null && rows[2].UnrealisedPnl == 0m, "Unknown portfolio margin must not become zero return");
        Check(new BybitOpenPosition("X", "Buy", 0, 1, InitialMargin: 0, UnrealisedPnl: 1).ReturnPercent is null, "Zero margin must not divide by zero");
    }
    public static void WalletSnapshot() => Wallet().GetAwaiter().GetResult();
    private static async Task Wallet()
    {
        var root = Path.Combine(Path.GetTempPath(), "wallet-snapshot-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var old = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH"); var path = Path.Combine(root,"queue.json");
        Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH", path);
        try
        {
            var store = new ServerOrderStore(path, Path.Combine(root,"history.db"));
            var now = DateTime.UtcNow.AddSeconds(-10);
            var s = new ServerSignal { Id=Guid.NewGuid(), Symbol="BTCUSDT", Direction="Long", RequestedByUsername="owner", Status="Completed", CreatedAtUtc=now, FilledAtUtc=now, CompletedAtUtc=now, TargetReachedAtUtc=now, Outcome="Target" };
            await store.AddAsync(s);
            var options = new DedicatedTelegramOptions("dedicated", null, null);
            var initial = await new MobileSignalFeed(store,options).HistoryAsync("owner",null,null,default);
            Check(initial.Events.Single(e=>e.Kind=="Target").WalletBalanceUsdt is null,"Missing historical balance must stay unknown");
            var calls=0;
            var client = new BybitDemoClient(new("fixture","fixture"),new HttpClient(new Handler(_=>{
                calls++;
                return new(HttpStatusCode.OK){Content=new StringContent("""{"retCode":0,"result":{"list":[{"coin":[{"coin":"USDT","walletBalance":"0"}]}]}}""")};
            })));
            await WalletNotification.CaptureAsync(client,store,s,default);
            await WalletNotification.CaptureAsync(client,store,s,default);
            Check(calls==1,"Result wallet must be captured once, including zero balance");
            var reopened = new ServerOrderStore(path,Path.Combine(root,"history.db"));
            var saved=(await reopened.GetAllAsync()).Single();
            Check(saved.ResultWalletBalanceUsdt==0m && saved.ResultWalletAtUtc.HasValue,"Wallet snapshot must survive cloning and restart on a terminal record");
            var enriched=await new MobileSignalFeed(reopened,options).HistoryAsync("owner",null,null,default);
            Check(enriched.Events.Single(e=>e.Kind=="Target").WalletBalanceUsdt==0m,"Existing history card must be enriched after asynchronous wallet capture");
            Check(enriched.Events.Single(e=>e.Kind=="Opened").WalletBalanceUsdt is null,"Only terminal results should carry wallet balance");
            await reopened.RemoveAsync(s.Id);
            var archived=await new MobileSignalFeed(reopened,options).HistoryAsync("owner",null,null,default);
            Check(archived.Events.Single(e=>e.Kind=="Target").WalletBalanceUsdt==0m,"Archived result balance must remain available");
        }
        finally { Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",old); SignalHistoryStore.ClearConnectionPools(); Directory.Delete(root,true); }
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send): HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(send(request)); }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}

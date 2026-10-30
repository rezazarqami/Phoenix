using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

internal static class ConnectionStatusTests
{
    public static void FreshnessAndPartialFailures()
    {
        var state=new ServerState();var at=DateTime.UtcNow;
        Check(state.Read(true,at).Status=="unknown","Startup must never claim a healthy connection");
        state.RecordPublic(true,null,at);state.RecordAccount(true,null,at);state.RecordMonitoring(true,null,at);
        Check(state.Read(true,at).Status=="connected","Only completed, recent independent checks may be healthy");
        Check(state.Read(false,at) is {Status:"unknown",Account.Status:"not_configured"},"Missing credentials must not appear as a connected account");
        state.BeginMonitoring(["BTCUSDT","FAILUSDT"]);state.RecordSymbol("FAILUSDT","symbol unavailable");
        Check(state.Read(true,at) is {Status:"degraded",PublicApi.Status:"connected",Account.Status:"connected",AffectedSymbolCount:1},
            "One symbol error is partial monitoring failure, not exchange or account disconnection");
        state.RecordSymbol("FAILUSDT",null);Check(state.Read(true,at).Status=="connected","Successful symbol retry must clear only that symbol's warning");
        state.RecordAccount(false,"Invalid key",at.AddSeconds(1));
        Check(state.Read(true,at.AddSeconds(1)) is {Status:"error",PublicApi.Status:"connected",Account.Status:"error"},"A failed account check must revoke an earlier success immediately");
        state.RecordAccount(true,null,at.AddSeconds(2));
        Check(state.Read(true,at.AddSeconds(46)) is {Status:"unknown",PublicApi.Status:"stale"},"Old public success must expire");
        state.RecordPublic(true,null,at.AddSeconds(91));
        Check(state.Read(true,at.AddSeconds(93)) is {Status:"unknown",Account.Status:"stale"},"Old authentication success must also expire");
        state.RecordAccount(true,null,at.AddSeconds(121));state.RecordPublic(true,null,at.AddSeconds(121));
        Check(state.Read(true,at.AddSeconds(121)) is {Status:"unknown",Monitoring.Status:"stale"},"A stalled trading monitor must not leave the aggregate healthy");
        state.RecordMonitoring(false,"monitor failed",at.AddSeconds(121));
        Check(state.Read(true,at.AddSeconds(121)).Status=="error","A failed complete monitoring cycle must be explicit");
        state.RecordSymbol("REMOVED","old failure");state.BeginMonitoring(["BTCUSDT"]);
        Check(state.Read(true,at).AffectedSymbolCount==0,"Removed symbols must not leave permanent false warnings");
    }
    public static void PeriodicReadOnlyChecks()=>Probe().GetAwaiter().GetResult();
    private static async Task Probe()
    {
        var calls=new List<string>();var badAccount=false;var badPublic=false;
        using var http=new HttpClient(new Handler(request=>{
            var path=request.RequestUri!.AbsolutePath;lock(calls)calls.Add(path);
            if(path=="/v5/market/time")return Reply(badPublic?"{\"retCode\":10006,\"retMsg\":\"Rate limit\"}":"{\"retCode\":0,\"result\":{}}");
            if(path=="/v5/account/wallet-balance")return Reply(badAccount?"{\"retCode\":10003,\"retMsg\":\"Invalid key\"}":"{\"retCode\":0,\"result\":{\"list\":[{\"totalEquity\":\"10\"}]}}");
            throw new Exception("Connectivity probes must never place or change orders: "+path);
        }));
        var options=new BybitDemoOptions("fixture-key","fixture-secret",true);
        var state=new ServerState();state.RecordMonitoring(true,null,DateTime.UtcNow);
        using var worker=new BybitConnectionWorker(new(options,http),options,state,NullLogger<BybitConnectionWorker>.Instance);
        var at=DateTime.UtcNow;
        await worker.CheckNowAsync(default,at);Check(state.Read(true).Status=="connected","Fresh public/account probes must confirm health");
        await worker.CheckNowAsync(default,at.AddSeconds(15));
        Check(calls.Count(p=>p.Contains("wallet"))==1,"Read-only account probes must be rate bounded");
        badAccount=true;await worker.CheckNowAsync(default,at.AddSeconds(46));
        Check(state.Read(true) is {Status:"error",Account.Status:"error",PublicApi.Status:"connected"},"Periodic revalidation must detect revoked credentials even after a prior successful login");
        badAccount=false;await worker.CheckNowAsync(default,at.AddSeconds(92));
        Check(state.Read(true).Status=="connected","A later confirmed account recovery must clear its failure");
        badPublic=true;await worker.CheckNowAsync(default,at.AddSeconds(93));
        Check(state.Read(true) is {Status:"error",Account.Status:"connected",PublicApi.Status:"error"},"Public failure must not be relabeled as account failure");
    }
    private static HttpResponseMessage Reply(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body)};
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(response(request));}
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}

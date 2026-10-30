using System.Text.Json;
using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SupportResistanceNetworkTests
{
    public static void Run() => Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var root=Path.Combine(Path.GetTempPath(),"phoenix-sr-network-"+Guid.NewGuid());
        Directory.CreateDirectory(root);
        var oldPath=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",Path.Combine(root,"queue.json"));
            var calls=new System.Collections.Concurrent.ConcurrentBag<string>();
            using var http=new HttpClient(new Handler(request => {
                var query=request.RequestUri!.Query;
                calls.Add(query);
                if(query.Contains("BTCUSDT"))throw new HttpRequestException("Unrelated market unavailable");
                var list=Enumerable.Range(0,1000).Select(i=>new[]{(i*60_000L).ToString(),"100","102","98","100","1","100"}).Reverse().ToArray();
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{retCode=0,result=new{list}}))};
            }));
            var client=new BybitDemoClient(new BybitDemoOptions(null,null),http);
            var store=new ServerOrderStore(Path.Combine(root,"queue.json"),Path.Combine(root,"history.db"));
            using var learning=new SignalLearningService(store,new ShadowSignalRuntime(),NullLogger<SignalLearningService>.Instance);
            var service=new ProfessionalSignalAnalysisService(client,new SignalSimilarityService(learning));
            var candles=Enumerable.Range(0,1000).Select(i=>new BybitKline(i*60_000L,100,102,98,100,1)).ToArray();
            var candidate=new SignalCandidate("ALTUSDT","15","Long",120,80,101,100,110,90,null,null,1,1,0,0,0,0,0,100,"fixture",false,null);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var analysis=await service.AnalyzeAsync(candidate,candles,"15",timeout.Token);
            if(calls.Count!=6 || calls.Any(x=>x.Contains("BTCUSDT")) || analysis.MarketRegime!="حمایت و مقاومت")
                throw new Exception("SR proposal must use only the six additional symbol intervals; an unavailable BTC route must not affect it.");
            calls.Clear();
            var known=new[]{"1","5","15","60","240"}.ToDictionary(x=>x,x=>(IReadOnlyList<BybitKline>)candles);
            var reusedService=new ProfessionalSignalAnalysisService(client,new SignalSimilarityService(learning));
            var reused=await reusedService.AnalyzeAsync(candidate,candles,"15",timeout.Token,knownHistories:known);
            if(calls.Count!=3 || JsonSerializer.Serialize(reused.Features)!=JsonSerializer.Serialize(analysis.Features))
                throw new Exception("Reusing full scanner histories must remove redundant fetches without changing technical measurements.");
            await reusedService.GetReviewCandlesAsync(candidate.Symbol,"240",known,timeout.Token);
            await reusedService.GetReviewCandlesAsync(candidate.Symbol,"D",known,timeout.Token);
            if(calls.Count!=3)throw new Exception("The review chart must reuse scan or analysis candles instead of fetching them again.");
        }
        finally {Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",oldPath);SignalHistoryStore.ClearConnectionPools();Directory.Delete(root,true);}
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(response(request));
    }
}

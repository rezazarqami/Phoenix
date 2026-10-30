using System.Net;
using System.Reflection;
using System.Collections.Concurrent;
using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Services;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ParallelScanTests
{
    public static void BoundedOverlapAndCancellation()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var root=Path.Combine(Path.GetTempPath(),"phoenix-parallel-scan-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sixReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls=new ConcurrentBag<string>();int active=0,peak=0,total=0;
            using var http=new HttpClient(new Handler(async(request,token)=>{
                var running=Interlocked.Increment(ref active);
                lock(calls)peak=Math.Max(peak,running);
                calls.Add(request.RequestUri!.PathAndQuery);
                if(Interlocked.Increment(ref total)==6)sixReady.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(token);
                    var body=request.RequestUri.AbsolutePath.Contains("instruments")
                        ? "{\"retCode\":0,\"result\":{\"list\":[{\"symbol\":\"TESTUSDT\",\"priceFilter\":{\"tickSize\":\"0.1\"},\"lotSizeFilter\":{\"qtyStep\":\"0.01\",\"minOrderQty\":\"0.01\",\"minNotionalValue\":\"5\"},\"leverageFilter\":{\"maxLeverage\":\"100\",\"minLeverage\":\"1\",\"leverageStep\":\"1\"}}]}}"
                        : "{\"retCode\":0,\"result\":{\"list\":[[\"1000\",\"100\",\"102\",\"98\",\"100\",\"1\"]]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)};
                }
                finally{Interlocked.Decrement(ref active);}
            }));
            var client=new BybitDemoClient(new(null,null),http);
            var store=new ServerOrderStore(Path.Combine(root,"queue.json"),Path.Combine(root,"history.db"));
            var service=new SignalBatchService(null!,client,new(new StrategyCalculator()),store,null!,null!,null!,null!,null!,null!,NullLogger<SignalBatchService>.Instance,null!);
            var method=typeof(SignalBatchService).GetMethod("ScanCandidateAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
            Task Scan(string symbol,CancellationToken token)=>(Task)method.Invoke(service,[symbol,10m,"All","All","All",new HashSet<string>(),token])!;
            Task[] Start(CancellationToken token)=>new[]{"FIRSTUSDT","SECONDUSDT","THIRDUSDT"}.Select(x=>Scan(x,token)).ToArray();
            var tasks=Start(default);await sixReady.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(calls.Any(x=>x.Contains("instruments"))&&calls.Any(x=>x.Contains("kline")),"Candle fetches must overlap instrument metadata instead of waiting for it");
            Check(peak==6,"Combined concurrent symbol/timeframe network activity must be bounded at six");
            release.SetResult();await Task.WhenAll(tasks);
            Check(total==18&&peak==6&&active==0,"Each of three symbols must fetch exactly one rule set and five timeframes, without an unbounded request burst");
            release=new(TaskCreationOptions.RunContinuationsAsynchronously);sixReady=new(TaskCreationOptions.RunContinuationsAsynchronously);total=0;
            using var cancel=new CancellationTokenSource();tasks=Start(cancel.Token);await sixReady.Task.WaitAsync(TimeSpan.FromSeconds(4));cancel.Cancel();
            try{await Task.WhenAll(tasks);throw new Exception("Expected cancelled scan");}catch(OperationCanceledException){}
            Check(active==0,"Cancellation must drain all active requests");
            release=new(TaskCreationOptions.RunContinuationsAsynchronously);sixReady=new(TaskCreationOptions.RunContinuationsAsynchronously);total=0;
            tasks=Start(default);await sixReady.Task.WaitAsync(TimeSpan.FromSeconds(4));release.SetResult();await Task.WhenAll(tasks);
            Check(active==0,"All request slots must remain usable after cancellation");
        }
        finally{SignalHistoryStore.ClearConnectionPools();Directory.Delete(root,true);}
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>response(request,token);}
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}

using System.Reflection;
using System.Net;
using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;

internal static class ScanPerformanceTests
{
    public static void OrderedFetch() => Exercise().GetAwaiter().GetResult();
    private static async Task Exercise()
    {
        var keys = new[] { "1", "5", "15", "60", "240" };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, peak = 0, count = 0;
        var work = OrderedConcurrentFetch.RunAsync(keys, async key => {
            int running = Interlocked.Increment(ref active);
            lock (keys) peak = Math.Max(peak, running);
            if (Interlocked.Increment(ref count) == 3) started.TrySetResult();
            await release.Task;
            await Task.Delay(key == "1" ? 30 : 1);
            Interlocked.Decrement(ref active);
            return key;
        }, 3, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (count != 3 || peak != 3) throw new Exception("Fetches must overlap within a three-request bound");
        release.SetResult();
        if (!(await work).SequenceEqual(keys) || peak != 3 || count != 5)
            throw new Exception("All inputs and their original evaluation order must be preserved");
        using var cancelled = new CancellationTokenSource();
        var blocked = OrderedConcurrentFetch.RunAsync(keys, async key => {
            await Task.Delay(Timeout.Infinite, cancelled.Token); return key;
        }, 3, cancelled.Token);
        cancelled.Cancel();
        try { await blocked; throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }

    public static void AnalysisFetchDeduplication() => CacheExercise().GetAwaiter().GetResult();
    private static async Task CacheExercise()
    {
        int calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async token => {
            Interlocked.Increment(ref calls); await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"retCode\":0,\"result\":{\"list\":[[\"1000\",\"1\",\"2\",\"1\",\"2\",\"1\"]]}}") };
        });
        var service = new ProfessionalSignalAnalysisService(new BybitDemoClient(new(null,null),new HttpClient(handler)),null!);
        var method = typeof(ProfessionalSignalAnalysisService).GetMethod("GetCandlesAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        Task<IReadOnlyList<BybitKline>> Fetch(CancellationToken token) =>
            (Task<IReadOnlyList<BybitKline>>)method.Invoke(service,["BTCUSDT","60",300,token])!;
        var first = Fetch(default); var second = Fetch(default);
        using var cancel = new CancellationTokenSource();
        var abandoned = Fetch(cancel.Token);cancel.Cancel();
        try { await abandoned; throw new Exception("Expected waiter cancellation"); } catch(OperationCanceledException) { }
        release.SetResult();
        var results = await Task.WhenAll(first,second);
        if(calls != 1 || !ReferenceEquals(results[0],results[1]))throw new Exception("Identical concurrent reads must share the existing cache");
        await Fetch(default);
        if(calls != 1)throw new Exception("Cache lifetime must be preserved");
    }
    private sealed class Handler(Func<CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(token);
    }
}

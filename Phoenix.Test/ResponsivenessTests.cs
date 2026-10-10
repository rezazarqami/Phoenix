using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Web;

internal static class ResponsivenessTests
{
    public static void ActiveCancellation()
    {
        var analyzer = new ElliottWaveAnalyzer();
        using var cancel = new CancellationTokenSource();
        var random = new Random(42);
        var price = 100m;
        var candles = Enumerable.Range(0, 1000).Select(i => {
            price += (decimal)(random.NextDouble() - .5);
            return new BybitKline(i * 60000L, price, price, price, price, 1);
        }).ToArray();
        var work = analyzer.AnalyzeAsync(candles, cancel.Token);
        Thread.Sleep(25);
        Check(!work.IsCompleted, "Noisy parse must be active before testing stop");
        var watch = Stopwatch.StartNew();
        cancel.Cancel();
        try { work.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); throw new Exception("Expected active cancellation"); }
        catch (OperationCanceledException) { }
        Console.WriteLine($"Active Elliott cancellation: {watch.ElapsedMilliseconds} ms");
        var small = candles.Take(35).ToArray();
        var asyncResult = analyzer.AnalyzeAsync(small, default).GetAwaiter().GetResult();
        Check(JsonSerializer.Serialize(asyncResult) == JsonSerializer.Serialize(analyzer.Analyze(small)), "Async analysis must preserve counts and release its slot");
    }

    public static void IndependentSymbolsAndCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "phoenix-responsive-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("PHOENIX_ELLIOTT_COUNT_DIR");
        Environment.SetEnvironmentVariable("PHOENIX_ELLIOTT_COUNT_DIR", root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monthly = Bars(40, "M");
        var weekly = Bars(40, "W");
        var calls = 0;
        var bybit = new BybitDemoClient(new(null, null), new HttpClient(new Handler(async (request, token) => {
            if (request.RequestUri!.Query.Contains("symbol=SLOWUSDT")) {
                entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token);
            }
            Interlocked.Increment(ref calls);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                retCode = 0, result = new { list = monthly.Reverse().Select(c => new[] { c.OpenTime.ToString(), c.Open.ToString(), c.High.ToString(), c.Low.ToString(), c.Close.ToString(), "1" }).ToArray() }
            })) };
        })));
        var store = new ElliottCountStore(bybit, new ElliottWaveAnalyzer(), NullLogger<ElliottCountStore>.Instance);
        using var cancel = new CancellationTokenSource();
        try {
            var blocked = store.AnalyzeAsync("SLOWUSDT", "15", weekly, cancel.Token);
            entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            store.AnalyzeAsync("FASTUSDT", "M", monthly, default).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            cancel.Cancel();
            try { blocked.GetAwaiter().GetResult(); throw new Exception("Expected cancelled symbol fetch"); }
            catch (OperationCanceledException) { }
            store.AnalyzeAsync("SLOWUSDT", "M", monthly, default).GetAwaiter().GetResult();
            store.AnalyzeAsync("CACHEUSDT", "W", weekly, default).GetAwaiter().GetResult();
            Check(calls == 1, "Initial higher tier must be fetched once");
            var changed = weekly.ToArray(); changed[^1] = changed[^1] with { Close = 200m };
            store.AnalyzeAsync("CACHEUSDT", "W", changed, default).GetAwaiter().GetResult();
            Check(calls == 1, "Repeated higher tiers must reuse recent bars");
            using var snapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "CACHEUSDT-W.json")));
            Check(snapshot.RootElement.GetProperty("candles")[changed.Length - 1].GetProperty("close").GetDecimal() == 200m,
                "Cache must not suppress a changed target candle");
        }
        finally { cancel.Cancel(); Environment.SetEnvironmentVariable("PHOENIX_ELLIOTT_COUNT_DIR", previous); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static void BatchStopAndRestart()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bybit = new BybitDemoClient(new(null, null), new HttpClient(new Handler(async (_, token) => {
            entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        })));
        var markets = new MarketCapCatalog(new HttpClient(), new BybitInstrumentCatalog(bybit), null!);
        var telegram = new TelegramNotifier(new("fake-token", "123"), new(null, null), null!, bybit, null!, NullLogger<TelegramNotifier>.Instance);
        var dedicated = new DedicatedTelegramNotifier(new("arman", null, null), NullLogger<DedicatedTelegramNotifier>.Instance);
        var batch = new SignalBatchService(markets, bybit, null!, null!, null!, telegram, dedicated,
            null!, null!, null!, new Lifetime(), NullLogger<SignalBatchService>.Instance, null!);
        Check(batch.Start(1, 10m, "All", "All", "All", 0, false, 30, null, out _), "Start must be accepted");
        entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        var watch = Stopwatch.StartNew();
        Check(batch.Stop(out _) && !batch.Status.Running, "Stop must update status immediately");
        Check(watch.ElapsedMilliseconds < 500, "Stop must not await market IO");
        var restarted = false;
        for (var i = 0; i < 200 && !restarted; i++) {
            restarted = batch.Start(1, 10m, "All", "All", "All", 0, false, 30, null, out _);
            if (!restarted) Thread.Sleep(10);
        }
        Check(restarted, "Cancelled IO must release the batch for restart");
        Check(batch.Stop(out _), "Restarted batch must also stop");
    }

    public static void MarketProviderTimeoutAndCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "phoenix-market-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bybit = new BybitDemoClient(new(null, null), new HttpClient(new Handler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "{\"retCode\":0,\"result\":{\"list\":[{\"symbol\":\"BTCUSDT\",\"status\":\"Trading\"},{\"symbol\":\"ETHUSDT\",\"status\":\"Trading\"}]}}") }))));
            var calls = 0;
            using var http = new HttpClient(new Handler(async (_, token) => {
                Interlocked.Increment(ref calls);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException();
            }));
            var orders = new ServerOrderStore(Path.Combine(root, "queue.json"), Path.Combine(root, "history.json"));
            var catalog = new MarketCapCatalog(http, new BybitInstrumentCatalog(bybit), orders);
            var watch = Stopwatch.StartNew();
            var assets = catalog.GetAsync(default).GetAwaiter().GetResult();
            Check(watch.Elapsed < TimeSpan.FromSeconds(6), "Optional market caps must not block the Bybit list");
            Check(assets.Count == 2 && assets.All(x => x.ActiveCount == 0), "Provider timeout must retain tradable symbols");
            orders.AddAsync(new ServerSignal { Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = "Long", Status = "Pending" })
                .GetAwaiter().GetResult();
            assets = catalog.GetAsync(default).GetAwaiter().GetResult();
            Check(calls == 1, "Repeated loads must reuse the market catalog");
            Check(assets.Single(x => x.Symbol == "BTCUSDT").ActiveLong == 1,
                "Cached market metadata must still refresh live signal counts");
        }
        finally { Directory.Delete(root, true); }
    }

    private static BybitKline[] Bars(int count, string interval) => Enumerable.Range(0, count).Select(i => {
        var date = interval == "M" ? new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(i)
            : new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i * 7);
        var price = 100m + i;
        return new BybitKline(date.ToUnixTimeMilliseconds(), price, price, price, price, 1);
    }).ToArray();
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
}

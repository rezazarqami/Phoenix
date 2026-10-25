using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Web;

internal static class ProposalLeverageCacheTests
{
    public static void DisplayAndDailyRefresh() => VerifyDisplay().GetAwaiter().GetResult();
    private static async Task VerifyDisplay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "phoenix-leverage-display-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "cache.json");
            var clock = new Clock(); var calls = 0; var fail = false; var maximum = "20";
            var client = new BybitDemoClient(new(null, null), new HttpClient(new Handler(request =>
            {
                Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v5/market/instruments-info");
                Interlocked.Increment(ref calls);
                if (fail) return new(HttpStatusCode.ServiceUnavailable);
                return Page("", Item("KASUSDT", maximum));
            })));
            using var cache = new ProposalLeverageCache(client, NullLogger<ProposalLeverageCache>.Instance, path, clock);
            var unknown = cache.Describe("KASUSDT", 100, 101);
            Check(unknown.Contains("لورج محاسبه‌شده: 50×") && unknown.Contains("نامشخص") && calls == 0);
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => cache.RefreshAsync()));
            Check(calls == 1);
            var text = cache.Describe("kasusdt", 100, 101);
            Check(text.Contains("حداکثر لورج ارز: 20×") && text.Contains("لورج قابل استفاده با این سقف: 20×"));
            Check(text.Contains("محدود به سقف ارز") && calls == 1);
            Check(cache.Describe("KASUSDT", 100, 95).Contains("لورج قابل استفاده با این سقف: 10×"));
            Check(cache.Describe("NEWUSDT", 100, 101).Contains("نامشخص") && calls == 1);
            clock.Now = clock.Now.AddHours(23); await cache.RefreshAsync(); Check(calls == 1);
            using var restored = new ProposalLeverageCache(client, NullLogger<ProposalLeverageCache>.Instance, path, clock);
            await restored.RefreshAsync(); Check(calls == 1 && restored.Describe("kasusdt", 100, 101).Contains("20×"));
            clock.Now = clock.Now.AddHours(1); fail = true;
            try { await cache.RefreshAsync(); throw new Exception("Expected refresh failure"); }
            catch (HttpRequestException) { }
            Check(cache.Describe("KASUSDT", 100, 101).Contains("بیش از ۲۴ ساعت"));
            Check(cache.Describe("KASUSDT", 100, 101).Contains("حداکثر لورج ارز: 20×"));
            fail = false; maximum = "25"; await cache.RefreshAsync();
            Check(cache.Describe("KASUSDT", 100, 101).Contains("حداکثر لورج ارز: 25×"));
            Check(!cache.Describe("KASUSDT", 100, 101).Contains("بیش از ۲۴ ساعت"));
        }
        finally { Directory.Delete(directory, true); }
    }

    public static void PaginationAndFailure() => VerifyPages().GetAwaiter().GetResult();
    private static async Task VerifyPages()
    {
        var calls = 0;
        var client = new BybitDemoClient(new(null, null), new HttpClient(new Handler(request =>
        {
            calls++;
            Check(request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("limit=1000"));
            if (calls == 1) return Page("next/a+b", Item("AAAUSDT", "20"), Item("CLOSEDUSDT", "50", "Closed"));
            Check(request.RequestUri!.Query.Contains("cursor=next%2Fa%2Bb", StringComparison.OrdinalIgnoreCase));
            return Page("", Item("BBBUSDT", "25"), Item("NOLEVERAGE", "0"));
        })));
        var catalog = await client.GetLinearLeverageCatalogAsync();
        Check(calls == 2 && catalog.Count == 2 && catalog["BBBUSDT"].MaximumLeverage == 25);
        var repeating = new BybitDemoClient(new(null, null), new HttpClient(new Handler(_ => Page("same", Item("AAAUSDT", "20")))));
        try { await repeating.GetLinearLeverageCatalogAsync(); throw new Exception("Repeated cursor accepted"); }
        catch (InvalidDataException) { }
        var empty = new BybitDemoClient(new(null, null), new HttpClient(new Handler(_ => Page(""))));
        try { await empty.GetLinearLeverageCatalogAsync(); throw new Exception("Empty catalog accepted"); }
        catch (InvalidDataException) { }
    }
    private static object Item(string symbol, string maximum, string status = "Trading") =>
        new { symbol, status, leverageFilter = new { maxLeverage = maximum, minLeverage = "1", leverageStep = "0.01" } };
    private static HttpResponseMessage Page(string cursor, params object[] items) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            { retCode = 0, result = new { list = items, nextPageCursor = cursor } }))
    };
    private static void Check(bool value) { if (!value) throw new Exception("Proposal leverage assertion failed"); }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(send(request));
    }
}

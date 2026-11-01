using System.Net;
using System.Text.Json;
using Phoenix.Web;

internal static class CommodityQuoteTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run() => Verify().GetAwaiter().GetResult();
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(respond));
    }
    private static async Task Verify()
    {
        var clock = new Clock(); var at = clock.Now.AddSeconds(-30); var calls = 0; var failGold = false; var brokenIranGold = false;
        var factory = new Factory(r => {
            Interlocked.Increment(ref calls);
            if (r.RequestUri!.Host == "api.gold-api.com")
                return failGold ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { symbol = "XAU", currency = "USD", price = 3000.12m, updatedAt = at })) };
            if (r.RequestUri!.Host == "call1.tgju.org") {
                var stamp = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran")).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { current = new Dictionary<string, object> {
                    ["price_dollar_rl"] = new { p = "2,715,000", ts = stamp }, ["price_eur"] = new { p = "3,038,300", ts = stamp }, ["geram18"] = new { p = brokenIranGold ? "invalid" : "255,134,000", ts = stamp }
                } })) };
            }
            Check(r.RequestUri.AbsoluteUri.Contains("BZ%3DF", StringComparison.OrdinalIgnoreCase), "Request must use Brent, never WTI");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { chart = new { result = new[] { new { meta = new { symbol = "BZ=F", currency = "USD", regularMarketPrice = 80.25m, regularMarketTime = at.ToUnixTimeSeconds() } } } } })) };
        });
        var quotes = new CommodityQuotes(factory, clock);
        var pages = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => quotes.ReadAsync(default)));
        Check(calls == 3, "Concurrent clients must share three cached upstream requests");
        var gold = pages[0].Single(q => q.Symbol == "XAU"); var oil = pages[0].Single(q => q.Symbol == "BRENT");
        Check(gold.Price == 3000.12m && gold.AtUtc == at && !gold.Delayed, "Gold keeps spot price and upstream timestamp");
        Check(oil.Price == 80.25m && oil.Delayed && oil.AtUtc!.Value.ToUnixTimeSeconds() == at.ToUnixTimeSeconds(), "Brent has explicit delayed status and source timestamp");
        Check(pages[0].Single(q => q.Symbol == "USD_TOMAN").Price == 271500m && pages[0].Single(q => q.Symbol == "EUR_TOMAN").Price == 303830m, "Free-market currency rates convert rial to toman exactly once");
        var iranGold = pages[0].Single(q => q.Symbol == "GOLD18_TOMAN");
        Check(iranGold.Price == 25513400m && iranGold.Currency == "TOMAN" && iranGold.AtUtc!.Value.ToUnixTimeSeconds() == at.ToUnixTimeSeconds(), "18-karat gold is per gram in toman, with source Tehran timestamp converted to UTC");
        failGold = true; clock.Now = clock.Now.AddSeconds(31); var partial = await quotes.ReadAsync(default);
        Check(partial[0].Price == gold.Price && partial[0].AtUtc == gold.AtUtc && partial[0].Error != null, "Upstream failure preserves the last price and its original timestamp with an error");
        Check(partial[1].Error == null && calls == 6, "One failed source must not suppress the other source");
        await quotes.ReadAsync(default); Check(calls == 6, "Failed attempts must be cached too");
        brokenIranGold = true; clock.Now = clock.Now.AddSeconds(31); var iranPartial = await quotes.ReadAsync(default);
        Check(iranPartial.Single(q => q.Symbol == "GOLD18_TOMAN").Error != null && iranPartial.Single(q => q.Symbol == "GOLD18_TOMAN").Price == iranGold.Price, "An invalid Iranian gold quote preserves the last valid amount");
        Check(iranPartial.Single(q => q.Symbol == "USD_TOMAN").Error == null && iranPartial.Single(q => q.Symbol == "EUR_TOMAN").Error == null, "A malformed Iranian asset cannot suppress valid currency rates");
        foreach (var json in new[] { "{\"symbol\":\"XAG\",\"currency\":\"USD\",\"price\":30,\"updatedAt\":\"2026-01-01T00:00:00Z\"}", "{\"symbol\":\"XAU\",\"currency\":\"USD\",\"price\":0,\"updatedAt\":\"2026-01-01T00:00:00Z\"}" }) {
            using var doc = JsonDocument.Parse(json); var rejected = false;
            try { CommodityQuotes.Parse("XAU", doc.RootElement); } catch (FormatException) { rejected = true; }
            Check(rejected, "Reject mismatched symbols and invalid prices");
        }
    }
}

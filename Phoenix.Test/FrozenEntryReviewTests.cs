using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Web;

internal static class FrozenEntryReviewTests
{
    public static void CurrentImageWithInitialEvidence() => Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var at = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var features = new TechnicalFeatureSnapshot(.5m, 1m, 0, 0, 0, 1, 1, .5m, .5m, 1, .5m);
        var support = new SimilarityEvidence("proposal", "proposal", 10, 8, 6, 2, 6, 2, 6, 2)
            { ComparisonVersion = 2, ComparableRiskFree = 3, SameTimeframeNearbyRiskFree = 3 };
        var prediction = new SignalSimilarityResult(82, 63, 21, 10, 8) { Evidence = support };
        var initial = new SignalProposalEvidence(at, "TESTUSDT", "Long", "15", 100, 110, 90, features, prediction);
        var signal = new ServerSignal { Id = Guid.NewGuid(), Symbol = "TESTUSDT", Direction = "Long",
            Timeframe = "15", Ceiling = 120, Floor = 80, EntryPrice = 100, TakeProfit = 110, StopLoss = 90,
            EntryTriggeredAtUtc = at.AddHours(2), ProposalEvidence = initial,
            TechnicalFeatures = features with { SupportDistanceAtr = 9 }, TargetSimilarityPercent = 1 };
        var calls = 0;
        var client = new BybitDemoClient(new(null, null), new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v5/market/kline" || !request.RequestUri.Query.Contains("interval=240"))
                throw new Exception("Entry image requested non-chart analysis data");
            calls++;
            var candles = Enumerable.Range(0, 80).Select(i => new[] {
                (new DateTimeOffset(at).ToUnixTimeMilliseconds() + i * 900000L).ToString(),
                "100", (105 + calls).ToString(), "95", (100 + calls).ToString(), "10", "1000" }).Reverse().ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(new { retCode = 0, result = new { list = candles } })) };
        })));
        var reviews = new EntrySignalReviewService(client, null!, null!, NullLogger<EntrySignalReviewService>.Instance);
        var first = await reviews.BuildCurrentAsync(signal, default);
        var second = await reviews.BuildCurrentAsync(signal, default);
        if (first.AiRefreshed || second.AiRefreshed || calls != 2 || first.Image.SequenceEqual(second.Image))
            throw new Exception($"Entry reviews must render changed chart data without reanalysis: calls={calls}, first={first.AiRefreshed}, second={second.AiRefreshed}, equal={first.Image.SequenceEqual(second.Image)}");
        if (signal.TechnicalFeatures != features || signal.TargetSimilarityPercent != 82 ||
            signal.StopSimilarityPercent != 63 || signal.SimilarityEvidence != support ||
            signal.ProposalEvidence != initial || signal.EntryTechnicalFeatures is not null)
            throw new Exception("Entry review changed frozen evidence or invented an entry fingerprint");
        var legacy = new ServerSignal { TechnicalFeatures = features, TargetSimilarityPercent = 72,
            SimilarityEvidence = support };
        EntrySignalReviewService.ApplyInitialEvidence(legacy);
        if (legacy.TargetSimilarityPercent != 72 || legacy.TechnicalFeatures != features)
            throw new Exception("Legacy evidence must stay unchanged without a proposal snapshot");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(respond(request));
    }
}

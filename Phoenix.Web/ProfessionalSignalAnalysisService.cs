using System.Collections.Concurrent;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed record ProfessionalSignalAnalysis(
    TechnicalFeatureSnapshot Features,
    SignalSimilarityResult Prediction,
    string MarketRegime,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Risks)
{
    public DateTime? ObservedAtUtc { get; init; }
}

/// <summary>Cached multi-timeframe and BTC context analysis used before human approval.</summary>
public sealed class ProfessionalSignalAnalysisService(
    BybitDemoClient bybit,
    SignalSimilarityService predictor)
{
    private static readonly string[] ContextIntervals = ["15", "60", "240", "D"];
    private static readonly string[] ExtendedIntervals = ["5", "W", "M"];
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ProfessionalSignalAnalysis> AnalyzeAsync(SignalCandidate candidate,
        IReadOnlyList<BybitKline> signalCandles, string signalInterval, CancellationToken token,
        DateTime? asOfUtc = null)
    {
        var observedAtUtc = asOfUtc ?? DateTime.UtcNow;
        var symbolTasks = ContextIntervals.ToDictionary(x => x,
            x => x == signalInterval && signalCandles.Count >= 720
                ? Task.FromResult(signalCandles) : GetCandlesAsync(candidate.Symbol, x, 1000, token));
        var extendedTasks = ExtendedIntervals.ToDictionary(x => x,
            x => x == signalInterval && signalCandles.Count >= 80
                ? Task.FromResult<IReadOnlyList<BybitKline>?>(signalCandles)
                : TryGetCandlesAsync(candidate.Symbol, x, token));
        var bitcoinTasks = new[] { "60", "240", "D" }.ToDictionary(x => x,
            x => GetCandlesAsync("BTCUSDT", x, 300, token));
        await Task.WhenAll(symbolTasks.Values.Concat<Task>(bitcoinTasks.Values)
            .Concat(extendedTasks.Values));

        IReadOnlyList<BybitKline> AtEntry(IReadOnlyList<BybitKline> candles, string interval) =>
            CandlesAvailableAt(candles, interval, observedAtUtc);
        signalCandles = AtEntry(signalCandles, signalInterval);
        var symbolHistory = symbolTasks.ToDictionary(x => x.Key, x => AtEntry(x.Value.Result, x.Key));

        var histories = symbolHistory;
        foreach (var (interval, task) in extendedTasks)
            if (task.Result is { } candles && AtEntry(candles, interval) is { Count: >= 80 } earlier)
                histories[interval] = earlier;
        histories[signalInterval] = signalCandles;
        var enriched = BuildFeatures(candidate, signalInterval, histories);
        var direction = candidate.Direction == "Long" ? 1m : -1m;
        var btc = bitcoinTasks.Select(x => TechnicalFeatureExtractor.Calculate(AtEntry(x.Value.Result, x.Key), candidate)).ToArray();
        enriched = enriched with { BitcoinMarketAlignment = btc.Average(x => DirectionalTrend(x, direction)) };
        var regime = enriched.MarketRegimeScore >= 0.62m ? "روند قدرتمند"
            : enriched.MarketRegimeScore <= 0.22m ? "رنج/فشرده" : "روند متوسط";
        var serverSignal = new ServerSignal
        {
            Symbol = candidate.Symbol, Direction = candidate.Direction, Timeframe = signalInterval,
            CreatedAtUtc = observedAtUtc,
            EntryTriggeredAtUtc = asOfUtc,
            ChartMode = "Professional", TechnicalFeatures = enriched
        };
        var prediction = await predictor.CalculateAsync(serverSignal, token);
        // Keep the technical fingerprint for comparison and learning without
        // generating generic positive/negative commentary for proposals.
        return new(enriched, prediction, regime, [], []) { ObservedAtUtc = observedAtUtc };
    }

    // One versioned extraction path for live entry reviews and historical reconstruction.
    internal static TechnicalFeatureSnapshot BuildFeatures(SignalCandidate candidate, string interval,
        IReadOnlyDictionary<string, IReadOnlyList<BybitKline>> histories)
    {
        var weights = new Dictionary<string, decimal> { ["15"] = 1m, ["60"] = 1.5m, ["240"] = 2m, ["D"] = 2.5m };
        if (ContextIntervals.Any(x => !histories.TryGetValue(x, out var candles) || candles.Count < 52))
            throw new InvalidOperationException("Incomplete closed-candle market context.");
        var snapshots = ContextIntervals.ToDictionary(x => x,
            x => TechnicalFeatureExtractor.Calculate(histories[x], candidate));
        var primary = snapshots.TryGetValue(interval, out var matched) ? matched
            : TechnicalFeatureExtractor.Calculate(histories[interval], candidate);
        var direction = candidate.Direction == "Long" ? 1m : -1m;
        decimal Weighted(Func<TechnicalFeatureSnapshot, decimal> select) =>
            snapshots.Sum(x => select(x.Value) * weights[x.Key]) / weights.Values.Sum();
        return primary with
        {
            HigherTimeframeAlignment = Weighted(f => DirectionalTrend(f, direction)),
            MarketRegimeScore = snapshots.Values.Average(f => Math.Abs(f.TrendStrength)),
            StructureScore = Weighted(f => f.StructureScore),
            LiquiditySweepScore = Weighted(f => f.LiquiditySweepScore),
            StructureBreakScore = Weighted(f => f.StructureBreakScore!.Value),
            StructureRetestScore = Weighted(f => f.StructureRetestScore!.Value),
            MultiScaleLevels = MultiScaleLevelExtractor.Calculate(histories, candidate),
            SimilarityContextVersion = 3 // Both proposal and entry use only closed candles.
        };
    }

    internal static IReadOnlyList<BybitKline> CandlesAvailableAt(
        IReadOnlyList<BybitKline> candles, string interval, DateTime asOfUtc)
    {
        var cutoff = new DateTimeOffset(asOfUtc.ToUniversalTime()).ToUnixTimeMilliseconds();
        return candles.Where(c => interval switch
        {
            "M" => new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTime).UtcDateTime
                .AddMonths(1)).ToUnixTimeMilliseconds() <= cutoff,
            "W" => c.OpenTime + 7L * 86_400_000 <= cutoff,
            "D" => c.OpenTime + 86_400_000 <= cutoff,
            _ when int.TryParse(interval, out var minutes) => c.OpenTime + minutes * 60_000L <= cutoff,
            _ => false
        }).ToArray();
    }

    public static string ExplainStop(TechnicalFeatureSnapshot? f)
    {
        if (f is null) return "دادهٔ فنی کافی برای علت‌یابی وجود ندارد.";
        if (f.HigherTimeframeAlignment < -0.2m) return "تعارض با روند تایم‌فریم‌های بالاتر";
        if (f.BitcoinMarketAlignment < -0.2m) return "حرکت بیت‌کوین برخلاف جهت سیگنال";
        if (f.StructureScore < 0.35m) return "ساختار نامناسب یا شکست ساختار خلاف جهت";
        if (f.LiquiditySweepScore < 0.35m) return "جمع‌آوری نقدینگی در خلاف جهت ورود";
        if (f.VolumeRatio < 0.75m) return "حجم ناکافی برای ادامه حرکت";
        return "غلبه حرکت مخالف پس از فعال‌شدن ورود";
    }

    private async Task<IReadOnlyList<BybitKline>?> TryGetCandlesAsync(string symbol, string interval,
        CancellationToken token)
    {
        try { return await GetCandlesAsync(symbol, interval, 1000, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; } // A new symbol may not have any weekly history yet.
    }

    private async Task<IReadOnlyList<BybitKline>> GetCandlesAsync(string symbol, string interval,
        int limit, CancellationToken token)
    {
        var key = $"{symbol}|{interval}|{limit}";
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.AtUtc < TimeSpan.FromSeconds(45))
            return cached.Candles;
        var candles = await bybit.GetKlinesAsync(symbol, interval, limit, token);
        _cache[key] = new(DateTime.UtcNow, candles);
        return candles;
    }

    private static decimal DirectionalTrend(TechnicalFeatureSnapshot f, decimal direction) =>
        Math.Clamp((f.TrendStrength * 0.45m + f.IchimokuPosition * 0.35m +
            f.IchimokuCloudBias * 0.20m) * direction, -1m, 1m);

    private sealed record CacheEntry(DateTime AtUtc, IReadOnlyList<BybitKline> Candles);
}

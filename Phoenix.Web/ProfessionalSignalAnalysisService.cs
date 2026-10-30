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

/// <summary>Cached support/resistance snapshots across timeframes, used before human approval.</summary>
public sealed class ProfessionalSignalAnalysisService(
    BybitDemoClient bybit,
    SignalSimilarityService predictor)
{
    private static readonly string[] ContextIntervals = ["15", "60", "240", "D"];
    private static readonly string[] ExtendedIntervals = ["5", "W", "M"];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ProfessionalSignalAnalysis> AnalyzeAsync(SignalCandidate candidate,
        IReadOnlyList<BybitKline> signalCandles, string signalInterval, CancellationToken token,
        DateTime? asOfUtc = null,
        IReadOnlyDictionary<string,IReadOnlyList<BybitKline>>? knownHistories = null)
    {
        var observedAtUtc = asOfUtc ?? DateTime.UtcNow;
        var symbolTasks = ContextIntervals.ToDictionary(x => x,
            x => knownHistories?.TryGetValue(x,out var known)==true
                ? Task.FromResult(known) : x == signalInterval && signalCandles.Count >= 720
                ? Task.FromResult(signalCandles) : GetCandlesAsync(candidate.Symbol, x, 1000, token));
        var extendedTasks = ExtendedIntervals.ToDictionary(x => x,
            x => knownHistories?.TryGetValue(x,out var known)==true
                ? Task.FromResult<IReadOnlyList<BybitKline>?>(known) : x == signalInterval && signalCandles.Count >= 80
                ? Task.FromResult<IReadOnlyList<BybitKline>?>(signalCandles)
                : TryGetCandlesAsync(candidate.Symbol, x, token));
        // SR comparison needs this symbol's pivots only. An unrelated BTC
        // endpoint must not delay or prevent a proposal from being reviewed.
        await Task.WhenAll(symbolTasks.Values.Concat<Task>(extendedTasks.Values));

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
        const string regime = "حمایت و مقاومت";
        var serverSignal = new ServerSignal
        {
            Symbol = candidate.Symbol, Direction = candidate.Direction, Timeframe = signalInterval,
            EntryPrice = candidate.EntryPrice, TakeProfit = candidate.TakeProfit, StopLoss = candidate.StopLoss,
            CreatedAtUtc = observedAtUtc,
            EntryTriggeredAtUtc = asOfUtc,
            ChartMode = "Professional", TechnicalFeatures = enriched
        };
        var prediction = await predictor.CalculateAsync(serverSignal, token);
        // Keep the technical fingerprint for comparison and learning without
        // generating generic positive/negative commentary for proposals.
        return new(enriched, prediction, regime, [], []) { ObservedAtUtc = observedAtUtc };
    }

    internal Task<IReadOnlyList<BybitKline>> GetReviewCandlesAsync(string symbol,string interval,
        IReadOnlyDictionary<string,IReadOnlyList<BybitKline>>? knownHistories,CancellationToken token) =>
        knownHistories?.TryGetValue(interval,out var known)==true ? Task.FromResult(known) : GetCandlesAsync(symbol,interval,1000,token);

    internal async Task<ProfessionalSignalAnalysis> RefreshSimilarityAsync(SignalCandidate candidate,
        string interval, ProfessionalSignalAnalysis analysis, CancellationToken token)
    {
        if (analysis.ObservedAtUtc is not { } observed) return analysis;
        var prediction = await predictor.CalculateAsync(new ServerSignal
        {
            Symbol=candidate.Symbol, Direction=candidate.Direction, Timeframe=interval,
            EntryPrice=candidate.EntryPrice, TakeProfit=candidate.TakeProfit, StopLoss=candidate.StopLoss,
            CreatedAtUtc=observed, TechnicalFeatures=analysis.Features
        }, token);
        return analysis with { Prediction=prediction };
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
            SimilarityContextVersion = 4 // Closed candles, plus uncapped per-trade-level geometry.
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
        var gate = _fetchGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            // Concurrent proposals share the existing 45-second cache. A failed or
            // cancelled fetch is never cached; the next caller can retry normally.
            if (_cache.TryGetValue(key, out cached) && DateTime.UtcNow - cached.AtUtc < TimeSpan.FromSeconds(45))
                return cached.Candles;
            var candles = await bybit.GetKlinesAsync(symbol, interval, limit, token);
            _cache[key] = new(DateTime.UtcNow, candles);
            TrimCache(DateTime.UtcNow);
            return candles;
        }
        finally { gate.Release(); }
    }

    internal int CachedHistoryCount => _cache.Count;
    internal void TrimCache(DateTime now)
    {
        // Expiry must release old candle arrays, not merely stop returning them.
        // A full-market scan previously retained every symbol/timeframe forever.
        var entries=(ICollection<KeyValuePair<string,CacheEntry>>)_cache;
        foreach(var item in _cache)
            if(now-item.Value.AtUtc>=TimeSpan.FromSeconds(45))entries.Remove(item);
        foreach(var item in _cache.OrderBy(x=>x.Value.AtUtc).Take(Math.Max(0,_cache.Count-128)).ToArray())
            entries.Remove(item); // Conditional removal cannot evict a refreshed entry.
    }

    private static decimal DirectionalTrend(TechnicalFeatureSnapshot f, decimal direction) =>
        Math.Clamp((f.TrendStrength * 0.45m + f.IchimokuPosition * 0.35m +
            f.IchimokuCloudBias * 0.20m) * direction, -1m, 1m);

    private sealed record CacheEntry(DateTime AtUtc, IReadOnlyList<BybitKline> Candles);
}

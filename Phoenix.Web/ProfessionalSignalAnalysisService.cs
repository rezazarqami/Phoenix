using System.Collections.Concurrent;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed record ProfessionalSignalAnalysis(
    TechnicalFeatureSnapshot Features,
    SignalSimilarityResult Prediction,
    string MarketRegime,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Risks);

/// <summary>Cached multi-timeframe and BTC context analysis used before human approval.</summary>
public sealed class ProfessionalSignalAnalysisService(
    BybitDemoClient bybit,
    SignalSimilarityService predictor)
{
    private static readonly string[] ContextIntervals = ["15", "60", "240", "D"];
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ProfessionalSignalAnalysis> AnalyzeAsync(SignalCandidate candidate,
        IReadOnlyList<BybitKline> signalCandles, string signalInterval, CancellationToken token)
    {
        var symbolTasks = ContextIntervals.ToDictionary(x => x,
            x => x == signalInterval ? Task.FromResult(signalCandles) : GetCandlesAsync(candidate.Symbol, x, token));
        var bitcoinTasks = new[] { "60", "240", "D" }.ToDictionary(x => x,
            x => GetCandlesAsync("BTCUSDT", x, token));
        await Task.WhenAll(symbolTasks.Values.Concat(bitcoinTasks.Values));

        var snapshots = ContextIntervals.Select(interval =>
            (Interval: interval, Features: TechnicalFeatureExtractor.Calculate(symbolTasks[interval].Result, candidate))).ToArray();
        var direction = candidate.Direction == "Long" ? 1m : -1m;
        var weights = new Dictionary<string, decimal> { ["15"] = 1m, ["60"] = 1.5m, ["240"] = 2m, ["D"] = 2.5m };
        var alignment = snapshots.Sum(x => DirectionalTrend(x.Features, direction) * weights[x.Interval]) / weights.Values.Sum();

        var btc = bitcoinTasks.Select(x => TechnicalFeatureExtractor.Calculate(x.Value.Result, candidate)).ToArray();
        var bitcoinAlignment = btc.Average(x => DirectionalTrend(x, direction));
        var primary = snapshots.FirstOrDefault(x => x.Interval == signalInterval).Features
            ?? TechnicalFeatureExtractor.Calculate(signalCandles, candidate);
        var regimeScore = snapshots.Average(x => Math.Abs(x.Features.TrendStrength));
        var regime = regimeScore >= 0.62m ? "روند قدرتمند" : regimeScore <= 0.22m ? "رنج/فشرده" : "روند متوسط";
        var structure = snapshots.Sum(x => x.Features.StructureScore * weights[x.Interval]) / weights.Values.Sum();
        var sweep = snapshots.Sum(x => x.Features.LiquiditySweepScore * weights[x.Interval]) / weights.Values.Sum();
        var enriched = primary with
        {
            HigherTimeframeAlignment = alignment,
            BitcoinMarketAlignment = bitcoinAlignment,
            MarketRegimeScore = regimeScore,
            StructureScore = structure,
            LiquiditySweepScore = sweep
        };
        var serverSignal = new ServerSignal
        {
            Symbol = candidate.Symbol, Direction = candidate.Direction, Timeframe = signalInterval,
            ChartMode = "Professional", TechnicalFeatures = enriched
        };
        var prediction = await predictor.CalculateAsync(serverSignal, token);
        var strengths = new List<string>();
        var risks = new List<string>();
        Explain(alignment, 0.25m, "هم‌جهتی تایم‌فریم‌های بالاتر", "تعارض با تایم‌فریم‌های بالاتر");
        Explain(bitcoinAlignment, 0.20m, "هم‌جهتی مناسب با بیت‌کوین", "جهت بیت‌کوین مخالف سیگنال");
        Explain(structure * 2m - 1m, 0.20m, "ساختار قیمت هم‌جهت است", "ساختار قیمت تأیید نمی‌کند");
        Explain(sweep * 2m - 1m, 0.28m, "جمع‌آوری نقدینگی به نفع ورود", "جمع‌آوری نقدینگی علیه ورود");
        Explain(primary.PriceActionScore * 2m - 1m, 0.20m, "پرایس‌اکشن ورود را تأیید می‌کند", "پرایس‌اکشن ورود ضعیف است");
        if (primary.FibonacciAlignment >= 0.7m) strengths.Add("هم‌ترازی مناسب فیبوناچی");
        else if (primary.FibonacciAlignment <= 0.3m) risks.Add("هم‌ترازی فیبوناچی ضعیف");
        if (primary.VolumeRatio >= 1.15m) strengths.Add("حجم بالاتر از میانگین");
        else if (primary.VolumeRatio < 0.75m) risks.Add("حجم تأییدکننده نیست");
        return new(enriched, prediction, regime, strengths.Take(4).ToArray(), risks.Take(4).ToArray());

        void Explain(decimal value, decimal threshold, string positive, string negative)
        {
            if (value >= threshold) strengths.Add(positive);
            else if (value <= -threshold) risks.Add(negative);
        }
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

    private async Task<IReadOnlyList<BybitKline>> GetCandlesAsync(string symbol, string interval,
        CancellationToken token)
    {
        var key = $"{symbol}|{interval}";
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.AtUtc < TimeSpan.FromSeconds(45))
            return cached.Candles;
        var candles = await bybit.GetKlinesAsync(symbol, interval, 300, token);
        _cache[key] = new(DateTime.UtcNow, candles);
        return candles;
    }

    private static decimal DirectionalTrend(TechnicalFeatureSnapshot f, decimal direction) =>
        Math.Clamp((f.TrendStrength * 0.45m + f.IchimokuPosition * 0.35m +
            f.IchimokuCloudBias * 0.20m) * direction, -1m, 1m);

    private sealed record CacheEntry(DateTime AtUtc, IReadOnlyList<BybitKline> Candles);
}

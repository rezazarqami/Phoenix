using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Web;

internal static class SimilarityTests
{
    private static TechnicalFeatureSnapshot Features => new(0.5m, 1m, 0m, 0m, 0m,
        1m, 1m, 0.5m, 0.5m, 1m, 0.5m);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void AllExamplesAndIndependentGroups()
    {
        var features = Features;
        var signal = new ServerSignal { Direction = "Long", Timeframe = "15", TechnicalFeatures = features };
        var close = Enumerable.Range(0, 13).SelectMany(_ => new[]
        {
            new LearnedSignalPattern(signal, "Target", features),
            new LearnedSignalPattern(signal, "StopLoss", features)
        }).ToArray();
        var baseline = SignalSimilarityService.CalculateFromPatterns(signal, close);
        var more = close.Concat(Enumerable.Range(0, 30).Select(_ =>
            new LearnedSignalPattern(signal, "Target", features with
            { IchimokuPosition = 0.8m, IchimokuCloudBias = 0.8m, SupportDistanceAtr = 2m }))).ToArray();
        var result = SignalSimilarityService.CalculateFromPatterns(signal, more);
        Check(result.CalibrationSampleCount == 56, "Every example must be counted beyond the former 24 cap");
        Check(result.TargetPercent < baseline.TargetPercent, "Examples beyond the former nearest-12 cap must affect the score");
        Check(result.StopPercent == baseline.StopPercent, "Changing the target class must not move the independent stop score");
        var duplicated = SignalSimilarityService.CalculateFromPatterns(signal, more.Concat(more.Where(x => x.Outcome == "Target")).ToArray());
        Check(result.TargetPercent == duplicated.TargetPercent && result.StopPercent == duplicated.StopPercent,
            "Class population alone must not bias independently normalized scores");
        var unsupported = close.Where(x => x.Outcome == "Target").Concat(Enumerable.Range(0, 20).Select(_ =>
            new LearnedSignalPattern(signal, "StopLoss", features with
            { IchimokuPosition = 1m, IchimokuCloudBias = 1m, SupportDistanceAtr = 10m, ResistanceDistanceAtr = 10m }))).ToArray();
        var insufficient = SignalSimilarityService.CalculateFromPatterns(signal, unsupported);
        Check(insufficient.TargetPercent is null && insufficient.StopPercent is null,
            "Many unrelated examples must not masquerade as sufficient nearby evidence");
        Check(insufficient.CalibrationSampleCount == unsupported.Length, "Unavailable scores must retain the number actually compared");
    }

    public static void ContextStructureAndExcludedIndicators()
    {
        var features = Features with { SimilarityContextVersion = 1, MarketRegimeScore = 0.8m,
            StructureScore = 1m, LiquiditySweepScore = 1m, StructureBreakScore = 1m, StructureRetestScore = 1m };
        var signal = new ServerSignal { Direction = "Long", Timeframe = "15" };
        double Similar(TechnicalFeatureSnapshot other, ServerSignal? sample = null) =>
            SignalSimilarityService.TechnicalSimilarity(signal, sample ?? signal, features, other);
        Check(Similar(features) == 1d, "Identical evidence must remain fully similar");
        Check(Similar(features with { MarketRegimeScore = 0.1m }) < 1d, "Ranging and trending contexts must differ");
        Check(Similar(features with { AtrPercent = 4m }) < 1d, "Different volatility regimes must differ");
        Check(Similar(features, new ServerSignal { Direction = "Long", Timeframe = "D" }) < 1d, "Different signal timeframes must differ");
        Check(Similar(features with { StructureScore = 0m, LiquiditySweepScore = 0m,
            StructureBreakScore = 0m, StructureRetestScore = 0m }) < 1d, "Opposite structural evidence must differ");
        Check(Similar(features with { Rsi14 = 0m, VolumeRatio = 3m, BitcoinMarketAlignment = -1m,
            HigherTimeframeAlignment = -1m, PriceActionScore = 0m }) == 1d,
            "Unapproved volume, RSI and directional alignment indicators must not enter scoring");
        var legacy = features with { SimilarityContextVersion = 0, MarketRegimeScore = 0m,
            StructureScore = 0m, LiquiditySweepScore = 0m, StructureBreakScore = null, StructureRetestScore = null };
        Check(Similar(legacy) == 1d, "Missing legacy context must not be interpreted as contrary evidence");
    }

    public static void BreakAndRetestUseConfirmedPastPivots()
    {
        var candles = Enumerable.Range(0, 10).Select(i => new BybitKline(i * 60_000L,
            9m, i == 3 ? 12m : i >= 7 ? 14m : 10m,
            i >= 8 ? 12m : 8m, i >= 7 ? 13m : 9m, 1m)).ToArray();
        var beforeBreak = TechnicalFeatureExtractor.StructureEvents(candles.Take(7).ToArray(), 1m, 1m);
        Check(beforeBreak == (0.5m, 0.5m), "Future breakout and retest cannot leak into earlier evidence");
        var atBreak = TechnicalFeatureExtractor.StructureEvents(candles.Take(8).ToArray(), 1m, 1m);
        Check(atBreak == (1m, 0.5m), "Close beyond a previously confirmed swing must record a break only");
        var retested = TechnicalFeatureExtractor.StructureEvents(candles, 1m, 1m);
        Check(retested == (1m, 1m), "A later touch and close back beyond the level must confirm its retest");
        Check(TechnicalFeatureExtractor.StructureEvents(candles, -1m, 1m) == (0m, 0m),
            "Structural evidence must be normalized to the signal direction");
    }
}

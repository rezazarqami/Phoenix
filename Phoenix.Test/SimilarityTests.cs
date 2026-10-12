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
        Check(insufficient.TargetPercent > insufficient.StopPercent,
            "A distant opposing class must retain its low resemblance instead of suppressing the comparison");
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

    public static void HistoricalDispersionAndEvidence()
    {
        var signal = new ServerSignal { Direction = "Long", Timeframe = "15" };
        var target = Features with { StopLevelStrength = 0.9m };
        var stop = Features with { StopLevelStrength = 0.1m };
        var patterns = Enumerable.Range(0, 20).SelectMany(_ => new[]
        { new LearnedSignalPattern(signal, "Target", target), new LearnedSignalPattern(signal, "StopLoss", stop) }).ToArray();
        var candidate = new ServerSignal { Direction = "Long", Timeframe = "15", TechnicalFeatures = target };
        var result = SignalSimilarityService.CalculateFromPatterns(candidate, patterns);
        Check(result.TargetPercent == 100m && result.StopPercent < 20m,
            "Constant shared readings must not bury the only distinguishing measurement");
        var ambiguous = SignalSimilarityService.CalculateFromPatterns(withFeatures(target with { StopLevelStrength = 0.5m }), patterns);
        Check(ambiguous.TargetPercent == ambiguous.StopPercent, "A midpoint must remain honestly ambiguous");
        var missing = SignalSimilarityService.CalculateFromPatterns(withFeatures(Features), patterns);
        Check(missing.TargetPercent is null && missing.StopPercent is null, "Missing distance evidence must abstain");
        var legacy = target with { SimilarityContextVersion = 0, MultiScaleLevels = [], MarketRegimeScore = 0m };
        Check(SignalOutcomeModel.Values(signal, legacy)[10] is null, "Unversioned historical context is missing, not zero");
        Check(SignalReviewText.Comparison(81m, 80m).Contains("نزدیک"), "Close resemblance must be explained");
        ServerSignal withFeatures(TechnicalFeatureSnapshot f) => new() { Direction = "Long", Timeframe = "15", TechnicalFeatures = f };
    }

    public static void TimeframeIdentityAndPastOnly()
    {
        var signal = new ServerSignal { Direction = "Long", Timeframe = "15" };
        MultiScaleLevelSnapshot Level(string interval, decimal strength) =>
            new(interval, 80, 0m, 0m, 0m, strength, 0m, 0m, 0m, 0m, 0m, 0m);
        var target = Features with { MultiScaleLevels = [Level("15", .9m), Level("D", .1m)] };
        var stop = Features with { MultiScaleLevels = [Level("15", .1m), Level("D", .9m)] };
        var rows = Enumerable.Range(0, 10).SelectMany(i => new[] {
            new LearnedSignalPattern(new ServerSignal { Id = Guid.NewGuid(), Direction = "Long", Timeframe = "15",
                CompletedAtUtc = new DateTime(2026, 1, 1).AddDays(i) }, "Target", target),
            new LearnedSignalPattern(new ServerSignal { Id = Guid.NewGuid(), Direction = "Long", Timeframe = "15",
                CompletedAtUtc = new DateTime(2026, 1, 1).AddDays(i) }, "StopLoss", stop) }).ToArray();
        var candidate = new ServerSignal { Direction = "Long", Timeframe = "15", TechnicalFeatures = target,
            EntryTriggeredAtUtc = new DateTime(2026, 2, 1) };
        var baseline = SignalSimilarityService.CalculateFromPatterns(candidate, rows);
        Check(baseline.TargetPercent == 100m && baseline.StopPercent < 20m,
            "Opposing timeframe readings cannot cancel through averaging");
        Check(!SignalOutcomeModel.Values(signal, target).SequenceEqual(SignalOutcomeModel.Values(signal, stop)),
            "Outcome learning must preserve timeframe identity too");
        var future = rows.Select(x => new LearnedSignalPattern(new ServerSignal { Id = Guid.NewGuid(),
            Direction = "Long", Timeframe = "15", CompletedAtUtc = new DateTime(2027, 1, 1) }, "StopLoss", target));
        var review = SignalSimilarityService.CalculateFromPatterns(candidate, rows.Concat(future).Concat(rows).ToArray());
        Check(review == baseline, "Future labels and duplicated IDs must not affect a past review");
        var wrongInterval = new ServerSignal { Direction = "Long", Timeframe = "D", TechnicalFeatures = target };
        Check(SignalSimilarityService.CalculateFromPatterns(wrongInterval, rows).TargetPercent is null,
            "Unrelated signal timeframes cannot supply nearby evidence");
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

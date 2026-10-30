using Phoenix.Engine.Exchanges.Bybit;
using System.Text.Json;
using Phoenix.Web;

internal static class SimilarityTests
{
    private static TechnicalFeatureSnapshot Features => new(0.5m, 1m, 0m, 0m, 0m,
        1m, 1m, 0.5m, 0.5m, 1m, 0.5m);
    private static bool SameData(object? a,object? b)=>JsonSerializer.Serialize(a)==JsonSerializer.Serialize(b);
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
        Check(Similar(features with { MarketRegimeScore = 0.1m }) == 1d, "Market regime is excluded from SR similarity");
        Check(Similar(features with { AtrPercent = 4m }) == 1d, "Volatility regime is excluded from SR similarity");
        Check(Similar(features, new ServerSignal { Direction = "Long", Timeframe = "D" }) == 1d, "Timeframe only partitions SR counts");
        Check(Similar(features with { StructureScore = 0m, LiquiditySweepScore = 0m,
            StructureBreakScore = 0m, StructureRetestScore = 0m }) == 1d, "Generic structure indicators are excluded from SR similarity");
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
        Check(result.TargetPercent == 100m && result.StopPercent < result.TargetPercent,
            "Explicit weights retain differences without dataset-dependent exaggeration");
        var ambiguous = SignalSimilarityService.CalculateFromPatterns(withFeatures(target with { StopLevelStrength = 0.5m }), patterns);
        Check(ambiguous.TargetPercent == ambiguous.StopPercent, "A midpoint must remain honestly ambiguous");
        var missing = SignalSimilarityService.CalculateFromPatterns(withFeatures(Features), patterns);
        Check(missing.TargetPercent.HasValue && missing.StopPercent.HasValue, "Missing optional fields must still compare shared legacy measurements");
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
        Check(baseline.TargetPercent == 100m && baseline.StopPercent < baseline.TargetPercent,
            "Matching interval/window readings must preserve technical differences");
        Check(!SignalOutcomeModel.Values(signal, target).SequenceEqual(SignalOutcomeModel.Values(signal, stop)),
            "Outcome learning must preserve timeframe identity too");
        var future = rows.Select(x => new LearnedSignalPattern(new ServerSignal { Id = Guid.NewGuid(),
            Direction = "Long", Timeframe = "15", CompletedAtUtc = new DateTime(2027, 1, 1) }, "StopLoss", target));
        var review = SignalSimilarityService.CalculateFromPatterns(candidate, rows.Concat(future).Concat(rows).ToArray());
        Check(SameData(review,baseline), "Future labels and duplicated IDs must not affect a past review");
        var wrongInterval = new ServerSignal { Direction = "Long", Timeframe = "D", TechnicalFeatures = target };
        Check(SignalSimilarityService.CalculateFromPatterns(wrongInterval, rows).TargetPercent > 0m,
            "A distant timeframe must not multiply technical resemblance by zero");
    }

    public static void SparseAndDistantHistoryRemainsComparable()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var current = Features with { SimilarityContextVersion = 3, EntryLevelStrength = 0.9m,
            StopLevelStrength = 0.8m, TargetLevelStrength = 0.7m };
        var candidate = new ServerSignal { Direction = "Long", Timeframe = "1",
            CreatedAtUtc = now, TechnicalFeatures = current };
        var rows = new[] { "Target", "StopLoss", "RiskFree" }.Select(outcome =>
            new LearnedSignalPattern(new ServerSignal { Id = Guid.NewGuid(), Direction = "Long",
                Timeframe = "D", CompletedAtUtc = now.AddDays(-1) }, outcome, Features)).ToArray();
        var result = SignalSimilarityService.CalculateFromPatterns(candidate, rows);
        Check(result.Evidence is { ComparisonVersion: 6, ComparableTargets: 1, ComparableStops: 1,
            ComparableRiskFree: 1, OtherTimeframeNearbyTargets: 1, OtherTimeframeNearbyStops: 1,
            OtherTimeframeNearbyRiskFree: 1, SameTimeframeNearbyTargets: 0 },
            "Sparse legacy snapshots and distant timeframes must retain all three outcome counts");
        Check(!SignalReviewText.Similarity(result).Contains("احتمال"),
            "Uncalibrated and archived probability fields must never be displayed");
        var none = SignalSimilarityService.CalculateFromPatterns(candidate, []);
        Check(SignalReviewText.Support(none.Evidence).Contains("سابقهٔ فنی قابل‌مقایسه"),
            "No comparable history must not masquerade as six measured zeros");
        Check(SignalReviewText.Outcome(99m, 1m, 1000) == "",
            "Even previously stored probabilities must stay hidden");
        Check(SignalBatchService.PassesOutcomeFilter(result, 99m),
            "A stale client probability threshold must not suppress proposals");
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

    public static void LearningReadsEntireArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "phoenix-learning-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var history = new SignalHistoryStore(Path.Combine(root, "queue.json"), Path.Combine(root, "history.db"));
            var at = DateTime.UtcNow.AddYears(-15);
            var signal = new ServerSignal { Id = Guid.NewGuid(), Symbol = "TESTUSDT", Direction = "Long",
                CreatedAtUtc = at, EntryTriggeredAtUtc = at.AddDays(1), CompletedAtUtc = at.AddDays(4),
                Outcome = "RiskFree", EntryPrice = 100, TakeProfit = 110, StopLoss = 90, EntryTechnicalFeatures = Features };
            history.UpsertAsync(signal, "Test").GetAwaiter().GetResult();
            Check(history.GetAsync(3650, 5000).GetAwaiter().GetResult().Count == 0,
                "Fixture must lie outside the old history window");
            Check(history.GetLearningAsync().GetAwaiter().GetResult().Single().Signal.Outcome == "RiskFree",
                "Learning archive must retain old completed observations without a date window");
        }
        finally { SignalHistoryStore.ClearConnectionPools(); Directory.Delete(root, true); }
    }

    public static void ConciseThreeOutcomeCounts()
    {
        var at = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        LearnedSignalPattern Row(string outcome, string timeframe, int day) => new(new ServerSignal
        {
            Id = Guid.NewGuid(), Symbol = "TESTUSDT", Direction = "Long", Timeframe = timeframe,
            EntryPrice = 100, TakeProfit = 110, StopLoss = 90,
            EntryTriggeredAtUtc = at.AddDays(day), CompletedAtUtc = at.AddDays(day + 3)
        }, outcome, Features);
        var rows = new[] { Row("Target", "15", 0), Row("StopLoss", "15", 1), Row("RiskFree", "15", 2),
            Row("Target", "D", 3), Row("StopLoss", "D", 4), Row("RiskFree", "D", 5) };
        var candidate = new ServerSignal { Direction = "Long", Timeframe = "15", TechnicalFeatures = Features,
            CreatedAtUtc = at.AddYears(6) };
        var result = SignalSimilarityService.CalculateFromPatterns(candidate, rows.Concat(rows).ToArray());
        Check(result.Evidence is { SameTimeframeNearbyTargets: 1, SameTimeframeNearbyStops: 1,
            SameTimeframeNearbyRiskFree: 1, OtherTimeframeNearbyTargets: 1,
            OtherTimeframeNearbyStops: 1, OtherTimeframeNearbyRiskFree: 1 },
            "All-time, slow outcomes and other timeframes must count once each, including risk-free");
        var text = SignalReviewText.Similarity(result);
        Check(!text.Contains("۳۰") && !text.Contains("شباهت به") && !text.Contains("دقیقه") &&
            !text.Contains("سربه‌سر") && text.Contains("ریسک‌فری") && !text.Contains("احتمال"),
            "Concise summary must not expose diagnostics or disguise similarity as probability");
        Check(SignalReviewText.Support(result.Evidence).Trim().Split('\n').Length == 2,
            "Historical evidence must occupy exactly two lines");
        var future = rows[0] with { Signal = new ServerSignal { Id = Guid.NewGuid(), Direction = "Long",
            Timeframe = "15", CompletedAtUtc = candidate.CreatedAtUtc.AddDays(1) } };
        Check(SignalSimilarityService.CalculateFromPatterns(candidate, rows.Append(future).ToArray()).Evidence is { } futureEvidence && SameData(futureEvidence,result.Evidence),
            "Unknown future outcomes must never alter counts");
        Check(SignalOutcomeModel.Train(rows) is null, "A few descriptive examples cannot activate probabilities");
    }

    public static void SupportAndEconomicDuplicates()
    {
        var at = new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
        var rows = Enumerable.Range(0,12).Select(i => new LearnedSignalPattern(new ServerSignal
        {
            Id=Guid.NewGuid(), Symbol="BTCUSDT", Direction="Long", Timeframe="15",
            EntryPrice=100,TakeProfit=110,StopLoss=90, EntryTriggeredAtUtc=at.AddDays(i),
            CompletedAtUtc=at.AddDays(i).AddHours(1)
        }, i%2==0 ? "Target":"StopLoss", Features)).ToArray();
        var candidate = new ServerSignal { Direction="Long",Timeframe="15",TechnicalFeatures=Features,
            CreatedAtUtc=at.AddDays(20) };
        var result=SignalSimilarityService.CalculateFromPatterns(candidate,rows);
        Check(result.Evidence is { SameTimeframeNearbyCount:12,HistoricalTargetRate:50m,ReferenceStage:"entry",QueryStage:"proposal" },
            "Historical support must report exact counts and mismatched evidence stages.");
        var first=rows[0];
        var duplicate=new LearnedSignalPattern(new ServerSignal { Id=Guid.NewGuid(),Symbol="BTCUSDT",Direction="Long",Timeframe="15",
            EntryPrice=100,TakeProfit=110,StopLoss=90,EntryTriggeredAtUtc=at.AddSeconds(15),CompletedAtUtc=at.AddHours(1) },"Target",Features);
        Check(SignalSimilarityService.CalculateFromPatterns(candidate,rows.Append(duplicate).ToArray()) is { } duplicated && SameData(duplicated,result),
            "Duplicated trade episodes with different IDs must not inflate evidence.");
        var conflict=duplicate with { Outcome="StopLoss" };
        Check(SignalPatternIdentity.Unique(rows.Append(conflict)).Length==11,
            "Conflicting outcomes for the same episode must be excluded rather than arbitrarily chosen.");
        Check(!SignalReviewText.Support(result.Evidence).Contains("٪"),
            "Historical outcome frequencies must not be presented as new-signal probabilities.");
    }
}

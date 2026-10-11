namespace Phoenix.Web;

/// <summary>Independent resemblance to completed target and stop examples.
/// Scores are not probabilities and need not sum to 100.</summary>
public sealed class SignalSimilarityService(SignalLearningService learning)
{
    public async Task<SignalSimilarityResult> CalculateAsync(ServerSignal candidate,
        CancellationToken token = default)
    {
        if (candidate.TechnicalFeatures is null) return new(null, null, 0, 0, 0);
        var snapshot = await learning.GetSnapshotAsync(token);
        return CalculateFromPatterns(candidate, snapshot.Patterns, token);
    }

    internal static SignalSimilarityResult CalculateFromPatterns(ServerSignal candidate,
        IReadOnlyList<LearnedSignalPattern> patterns, CancellationToken token = default)
    {
        if (candidate.TechnicalFeatures is null) return new(null, null, 0, 0, 0);
        var targets = patterns.Where(x => x.Outcome == "Target").ToArray();
        var stops = patterns.Where(x => x.Outcome == "StopLoss").ToArray();
        if (targets.Length < 5 || stops.Length < 5)
            return new(null, null, patterns.Count, targets.Length, stops.Length);
        // Every valid entry-time example contributes. Exponential weights make
        // distant examples less influential without a fixed nearest-N cutoff.
        var targetEvidence = Score(targets);
        var stopEvidence = Score(stops);
        var compared = targets.Length + stops.Length;
        if (targetEvidence.CloseCount < 5 || stopEvidence.CloseCount < 5)
            return new(null, null, patterns.Count, targets.Length, stops.Length, compared);
        return new(Math.Round((decimal)(targetEvidence.Score * 100d), 0),
            Math.Round((decimal)(stopEvidence.Score * 100d), 0), patterns.Count,
            targets.Length, stops.Length, compared);

        (double Score, int CloseCount) Score(LearnedSignalPattern[] group)
        {
            double weighted = 0d, weights = 0d;
            var closeCount = 0;
            foreach (var example in group)
            {
                token.ThrowIfCancellationRequested();
                var similarity = TechnicalSimilarity(candidate, example.Signal,
                    candidate.TechnicalFeatures, example.Features);
                // Initial transparent settings, not fitted or calibrated probabilities.
                var weight = Math.Exp(-8d * (1d - similarity));
                weighted += similarity * weight;
                weights += weight;
                if (similarity >= 0.65d) closeCount++;
            }
            return (weighted / weights, closeCount);
        }
    }

    // Existing examples without level-specific fields still contribute through
    // their saved support/resistance distances and Ichimoku cloud readings.
    internal static double TechnicalSimilarity(ServerSignal candidate, ServerSignal sample,
        TechnicalFeatureSnapshot candidateFeatures, TechnicalFeatureSnapshot sampleFeatures)
    {
        var left = Directional(candidate, candidateFeatures);
        var right = Directional(sample, sampleFeatures);
        var weighted = 0d;
        var totalWeight = 0d;
        Add(Near(left.CloudPosition, right.CloudPosition, 2m), 18d);
        Add(Near(left.CloudBias, right.CloudBias, 2m), 18d);
        Add(Near(left.SupportRoom, right.SupportRoom, 3m), 14d);
        Add(Near(left.ResistanceRoom, right.ResistanceRoom, 3m), 14d);
        AddNullable(candidateFeatures.EntryLevelStrength, sampleFeatures.EntryLevelStrength, 8d);
        AddNullable(candidateFeatures.StopLevelStrength, sampleFeatures.StopLevelStrength, 8d);
        AddNullable(candidateFeatures.TargetLevelStrength, sampleFeatures.TargetLevelStrength, 8d);
        AddNullable(left.EntryCloud, right.EntryCloud, 5d, 2m);
        AddNullable(left.StopCloud, right.StopCloud, 5d, 2m);
        AddNullable(left.TargetCloud, right.TargetCloud, 5d, 2m);
        var sign = candidate.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
        var otherSign = sample.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
        var candidateScales = candidateFeatures.MultiScaleLevels;
        var sampleScales = sampleFeatures.MultiScaleLevels;
        if (candidateScales is { Count: > 0 } && sampleScales is { Count: > 0 })
        {
            var matches = candidateScales.Join(sampleScales,
                x => (x.Interval, x.Window), x => (x.Interval, x.Window),
                (a, b) => (Left: a, Right: b)).ToArray();
            if (matches.Length > 0)
            {
                Add(matches.Average(pair => (
                    Near(pair.Left.EntryStrength, pair.Right.EntryStrength, 1m) +
                    Near(pair.Left.StopStrength, pair.Right.StopStrength, 1m) +
                    Near(pair.Left.TargetStrength, pair.Right.TargetStrength, 1m)) / 3d), 24d);
                Add(matches.Average(pair => (
                    Near(pair.Left.CloudEntry * sign, pair.Right.CloudEntry * otherSign, 2m) +
                    Near(pair.Left.CloudStop * sign, pair.Right.CloudStop * otherSign, 2m) +
                    Near(pair.Left.CloudTarget * sign, pair.Right.CloudTarget * otherSign, 2m) +
                    Near(pair.Left.CloudBias * sign, pair.Right.CloudBias * otherSign, 2m)) / 4d), 18d);
                Add(matches.Average(pair => (
                    Near(pair.Left.FibonacciEntry, pair.Right.FibonacciEntry, 1m) +
                    Near(pair.Left.FibonacciStop, pair.Right.FibonacciStop, 1m) +
                    Near(pair.Left.FibonacciTarget, pair.Right.FibonacciTarget, 1m)) / 3d), 16d);
            }
        }
        // Context matching is relative to historical examples, not a rule
        // that automatically rewards a strong trend or a particular volatility.
        AddOptional(TimeframeSimilarity(candidate.Timeframe, sample.Timeframe), 10d);
        if (candidateFeatures.AtrPercent > 0m && sampleFeatures.AtrPercent > 0m)
            Add(Math.Clamp(1d - Math.Abs(Math.Log((double)(candidateFeatures.AtrPercent /
                sampleFeatures.AtrPercent))) / Math.Log(4d), 0d, 1d), 10d);
        if (HasContext(candidateFeatures) && HasContext(sampleFeatures))
        {
            Add(Near(candidateFeatures.MarketRegimeScore, sampleFeatures.MarketRegimeScore, 1m), 10d);
            Add(Near(candidateFeatures.StructureScore, sampleFeatures.StructureScore, 1m), 10d);
            Add(Near(candidateFeatures.LiquiditySweepScore, sampleFeatures.LiquiditySweepScore, 1m), 8d);
        }
        AddNullable(candidateFeatures.StructureBreakScore, sampleFeatures.StructureBreakScore, 6d);
        AddNullable(candidateFeatures.StructureRetestScore, sampleFeatures.StructureRetestScore, 6d);
        return totalWeight == 0d ? 0d : weighted / totalWeight;


        void Add(double value, double weight)
        {
            weighted += Math.Clamp(value, 0d, 1d) * weight;
            totalWeight += weight;
        }
        void AddOptional(double? value, double weight)
        {
            if (value.HasValue) Add(value.Value, weight);
        }
        void AddNullable(decimal? a, decimal? b, double weight, decimal scale = 1m)
        {
            if (a.HasValue && b.HasValue) Add(Near(a.Value, b.Value, scale), weight);
        }
    }

    private static bool HasContext(TechnicalFeatureSnapshot features) =>
        features.SimilarityContextVersion >= 1 || features.MultiScaleLevels is { Count: > 0 };

    private static double? TimeframeSimilarity(string? left, string? right)
    {
        static double Minutes(string? interval) => interval switch
        {
            "D" => 1440d, "W" => 10080d, "M" => 43200d,
            _ when double.TryParse(interval, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0d => value,
            _ => 0d
        };
        var a = Minutes(left);
        var b = Minutes(right);
        return a <= 0d || b <= 0d ? null : Math.Clamp(1d - Math.Abs(Math.Log(a / b)) / Math.Log(16d), 0d, 1d);
    }

    private static DirectionalFeatures Directional(ServerSignal signal, TechnicalFeatureSnapshot f)
    {
        var isLong = signal.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var sign = isLong ? 1m : -1m;
        // The level's cloud position is comparable before and at entry;
        // the current candle's cloud position is not the planned entry.
        return new((f.IchimokuEntryPosition ?? f.IchimokuPosition) * sign, f.IchimokuCloudBias * sign,
            isLong ? f.SupportDistanceAtr : f.ResistanceDistanceAtr,
            isLong ? f.ResistanceDistanceAtr : f.SupportDistanceAtr,
            f.IchimokuEntryPosition * sign, f.IchimokuStopPosition * sign,
            f.IchimokuTargetPosition * sign);
    }

    private static double Near(decimal left, decimal right, decimal scale) =>
        (double)Math.Clamp(1m - Math.Abs(left - right) / scale, 0m, 1m);

    private sealed record DirectionalFeatures(decimal CloudPosition, decimal CloudBias,
        decimal SupportRoom, decimal ResistanceRoom, decimal? EntryCloud,
        decimal? StopCloud, decimal? TargetCloud);
}

public sealed record SignalSimilarityResult(decimal? TargetPercent, decimal? StopPercent,
    int SampleCount, int TargetSampleCount, int StopSampleCount, int CalibrationSampleCount = 0);

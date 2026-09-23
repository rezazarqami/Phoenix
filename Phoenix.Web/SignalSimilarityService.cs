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
        return CalculateFromPatterns(candidate, snapshot.Patterns);
    }

    internal static SignalSimilarityResult CalculateFromPatterns(ServerSignal candidate,
        IReadOnlyList<LearnedSignalPattern> patterns)
    {
        if (candidate.TechnicalFeatures is null) return new(null, null, 0, 0, 0);
        var targets = patterns.Where(x => x.Outcome == "Target").ToArray();
        var stops = patterns.Where(x => x.Outcome == "StopLoss").ToArray();
        if (targets.Length == 0 || stops.Length == 0)
            return new(null, null, patterns.Count, targets.Length, stops.Length);
        // Score the nearest examples of each class independently. The larger
        // historical class cannot force the other score to be its complement.
        var targetScore = Score(targets);
        var stopScore = Score(stops);
        return new(Math.Round((decimal)(targetScore * 100d), 0),
            Math.Round((decimal)(stopScore * 100d), 0), patterns.Count,
            targets.Length, stops.Length, Math.Min(targets.Length, 12) + Math.Min(stops.Length, 12));

        double Score(LearnedSignalPattern[] group) => group
            .Select(x => TechnicalSimilarity(candidate, x.Signal, candidate.TechnicalFeatures, x.Features))
            .OrderByDescending(x => x).Take(12).Average();
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
        return totalWeight == 0d ? 0d : weighted / totalWeight;


        void Add(double value, double weight)
        {
            weighted += Math.Clamp(value, 0d, 1d) * weight;
            totalWeight += weight;
        }
        void AddNullable(decimal? a, decimal? b, double weight, decimal scale = 1m)
        {
            if (a.HasValue && b.HasValue) Add(Near(a.Value, b.Value, scale), weight);
        }
    }

    private static DirectionalFeatures Directional(ServerSignal signal, TechnicalFeatureSnapshot f)
    {
        var isLong = signal.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var sign = isLong ? 1m : -1m;
        return new(f.IchimokuPosition * sign, f.IchimokuCloudBias * sign,
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

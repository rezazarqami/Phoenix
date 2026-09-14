namespace Phoenix.Web;

/// <summary>Predicts the mutually-exclusive Target/StopLoss outcome from the technical state.</summary>
public sealed class SignalSimilarityService(SignalLearningService learning)
{
    private const int NeighbourLimit = 30;
    private const double PriorStrength = 8d;

    public async Task<SignalSimilarityResult> CalculateAsync(ServerSignal candidate,
        CancellationToken token = default)
    {
        if (candidate.TechnicalFeatures is null) return new(null, null, 0, 0, 0);
        var snapshot = await learning.GetSnapshotAsync(token);
        var targetCount = snapshot.Patterns.Count(x => x.Outcome == "Target");
        var stopCount = snapshot.Patterns.Count - targetCount;
        var neighbours = snapshot.Patterns
            .Select(x => new { Pattern = x, Similarity = TechnicalSimilarity(candidate, x.Signal,
                candidate.TechnicalFeatures, x.Features) })
            .OrderByDescending(x => x.Similarity)
            .Take(NeighbourLimit)
            .ToArray();

        // Shrink early predictions toward a deterministic technical prior. Relevant
        // completed results progressively outweigh it as the labelled history grows.
        var targetEvidence = TechnicalPrior(candidate) * PriorStrength;
        var evidenceWeight = PriorStrength;
        foreach (var neighbour in neighbours)
        {
            var relevance = Math.Pow(neighbour.Similarity, 4d);
            if (relevance < 0.05d) continue;
            targetEvidence += (neighbour.Pattern.Outcome == "Target" ? 1d : 0d) * relevance;
            evidenceWeight += relevance;
        }
        var probability = Math.Clamp(targetEvidence / evidenceWeight, 0.05d, 0.95d);
        var targetPercent = Math.Round((decimal)(probability * 100d), 1);
        return new(targetPercent, 100m - targetPercent, snapshot.Patterns.Count, targetCount, stopCount);
    }

    // Trade geometry (target, stop, range and position size) is deliberately excluded.
    internal static double TechnicalSimilarity(ServerSignal candidate, ServerSignal sample,
        TechnicalFeatureSnapshot candidateFeatures, TechnicalFeatureSnapshot sampleFeatures)
    {
        var left = Directional(candidate, candidateFeatures);
        var right = Directional(sample, sampleFeatures);
        var weighted = 0d;
        var totalWeight = 0d;
        Add(Near(left.Rsi, right.Rsi, 1m), 8d);
        Add(Near(candidateFeatures.AtrPercent, sampleFeatures.AtrPercent, 5m), 7d);
        Add(Near(left.Trend, right.Trend, 2m), 12d);
        Add(Near(left.CloudPosition, right.CloudPosition, 2m), 11d);
        Add(Near(left.CloudBias, right.CloudBias, 2m), 9d);
        Add(Near(left.SupportRoom, right.SupportRoom, 10m), 9d);
        Add(Near(left.ResistanceRoom, right.ResistanceRoom, 10m), 9d);
        Add(Near(candidateFeatures.FibonacciAlignment, sampleFeatures.FibonacciAlignment, 1m), 8d);
        Add(Near(candidateFeatures.PriceActionScore, sampleFeatures.PriceActionScore, 1m), 12d);
        Add(Near(candidateFeatures.VolumeRatio, sampleFeatures.VolumeRatio, 3m), 6d);
        Add(Near(candidateFeatures.ImpulseEfficiency, sampleFeatures.ImpulseEfficiency, 1m), 9d);
        if (!string.IsNullOrWhiteSpace(candidate.Timeframe) && !string.IsNullOrWhiteSpace(sample.Timeframe))
            Add(candidate.Timeframe.Equals(sample.Timeframe, StringComparison.OrdinalIgnoreCase) ? 1d : 0.65d, 3d);
        return totalWeight == 0d ? 0d : weighted / totalWeight;

        void Add(double value, double weight)
        {
            weighted += Math.Clamp(value, 0d, 1d) * weight;
            totalWeight += weight;
        }
    }

    private static double TechnicalPrior(ServerSignal signal)
    {
        var f = signal.TechnicalFeatures!;
        var d = Directional(signal, f);
        var score = (double)d.Trend * 0.75d + (double)d.CloudPosition * 0.55d +
            (double)d.CloudBias * 0.45d + ((double)f.PriceActionScore - 0.5d) * 1.4d +
            ((double)f.FibonacciAlignment - 0.5d) * 0.45d +
            ((double)f.ImpulseEfficiency - 0.5d) * 0.75d +
            Math.Clamp(((double)f.VolumeRatio - 1d) / 2d, -0.5d, 0.5d) * 0.35d +
            Math.Clamp(((double)d.ResistanceRoom - (double)d.SupportRoom) / 10d, -1d, 1d) * 0.45d +
            Math.Clamp(((double)d.Rsi - 0.5d) * 2d, -1d, 1d) * 0.25d;
        return 1d / (1d + Math.Exp(-score));
    }

    private static DirectionalFeatures Directional(ServerSignal signal, TechnicalFeatureSnapshot f)
    {
        var isLong = signal.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var sign = isLong ? 1m : -1m;
        return new(isLong ? f.Rsi14 : 1m - f.Rsi14, f.TrendStrength * sign,
            f.IchimokuPosition * sign, f.IchimokuCloudBias * sign,
            isLong ? f.SupportDistanceAtr : f.ResistanceDistanceAtr,
            isLong ? f.ResistanceDistanceAtr : f.SupportDistanceAtr);
    }

    private static double Near(decimal left, decimal right, decimal scale) =>
        (double)Math.Clamp(1m - Math.Abs(left - right) / scale, 0m, 1m);

    private sealed record DirectionalFeatures(decimal Rsi, decimal Trend, decimal CloudPosition,
        decimal CloudBias, decimal SupportRoom, decimal ResistanceRoom);
}

public sealed record SignalSimilarityResult(decimal? TargetPercent, decimal? StopPercent,
    int SampleCount, int TargetSampleCount, int StopSampleCount);

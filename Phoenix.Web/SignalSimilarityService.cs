namespace Phoenix.Web;

/// <summary>Produces two independent technical-pattern similarity scores.</summary>
public sealed class SignalSimilarityService(SignalLearningService learning)
{
    public async Task<SignalSimilarityResult> CalculateAsync(ServerSignal candidate,
        CancellationToken token = default)
    {
        var snapshot = await learning.GetSnapshotAsync(token);
        var targets = snapshot.Patterns.Where(x => x.Outcome == "Target").ToArray();
        var stops = snapshot.Patterns.Where(x => x.Outcome == "StopLoss").ToArray();
        return new(Score(candidate, targets), Score(candidate, stops),
            snapshot.Patterns.Count, targets.Length, stops.Length);
    }

    private static decimal? Score(ServerSignal candidate, IReadOnlyCollection<LearnedSignalPattern> patterns)
    {
        if (candidate.TechnicalFeatures is null || patterns.Count == 0) return null;
        var nearest = patterns
            .Select(x => Similarity(candidate, x.Signal, candidate.TechnicalFeatures, x.Features))
            .OrderByDescending(x => x)
            .Take(Math.Min(12, patterns.Count))
            .ToArray();
        if (nearest.Length == 0) return null;
        var weighted = 0d;
        var weights = 0d;
        for (var i = 0; i < nearest.Length; i++)
        {
            var rankWeight = Math.Exp(-i / 4d);
            weighted += nearest[i] * rankWeight;
            weights += rankWeight;
        }
        return Math.Round((decimal)(100d * weighted / weights), 1);
    }

    internal static double Similarity(ServerSignal candidate, ServerSignal sample,
        TechnicalFeatureSnapshot? candidateFeatures = null, TechnicalFeatureSnapshot? sampleFeatures = null)
    {
        var weighted = 0d;
        var totalWeight = 0d;
        Add(candidate.Direction == sample.Direction ? 1d : 0d, 8d);
        AddOptionalText(candidate.Timeframe, sample.Timeframe, 3d);
        Add(Closeness(RangeRatio(candidate), RangeRatio(sample)), 3d);
        Add(Closeness(TargetRatio(candidate), TargetRatio(sample)), 2d);
        Add(Closeness(StopRatio(candidate), StopRatio(sample)), 2d);
        if (candidateFeatures is not null && sampleFeatures is not null)
        {
            Add(Near(candidateFeatures.Rsi14, sampleFeatures.Rsi14, 1m), 8d);
            Add(Near(candidateFeatures.AtrPercent, sampleFeatures.AtrPercent, 5m), 7d);
            Add(Near(candidateFeatures.TrendStrength, sampleFeatures.TrendStrength, 2m), 10d);
            Add(Near(candidateFeatures.IchimokuPosition, sampleFeatures.IchimokuPosition, 2m), 10d);
            Add(Near(candidateFeatures.IchimokuCloudBias, sampleFeatures.IchimokuCloudBias, 2m), 7d);
            Add(Near(candidateFeatures.SupportDistanceAtr, sampleFeatures.SupportDistanceAtr, 10m), 9d);
            Add(Near(candidateFeatures.ResistanceDistanceAtr, sampleFeatures.ResistanceDistanceAtr, 10m), 9d);
            Add(Near(candidateFeatures.FibonacciAlignment, sampleFeatures.FibonacciAlignment, 1m), 8d);
            Add(Near(candidateFeatures.PriceActionScore, sampleFeatures.PriceActionScore, 1m), 9d);
            Add(Near(candidateFeatures.VolumeRatio, sampleFeatures.VolumeRatio, 3m), 5d);
            Add(Near(candidateFeatures.ImpulseEfficiency, sampleFeatures.ImpulseEfficiency, 1m), 8d);
        }
        return totalWeight == 0d ? 0d : weighted / totalWeight;

        void Add(double value, double weight)
        {
            weighted += Math.Clamp(value, 0d, 1d) * weight;
            totalWeight += weight;
        }
        void AddOptionalText(string? left, string? right, double weight)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return;
            Add(left.Equals(right, StringComparison.OrdinalIgnoreCase) ? 1d : 0d, weight);
        }
        static double Near(decimal left, decimal right, decimal scale) =>
            (double)Math.Clamp(1m - Math.Abs(left - right) / scale, 0m, 1m);
    }

    private static double RangeRatio(ServerSignal x) => Ratio(x.Ceiling - x.Floor, x.EntryPrice);
    private static double TargetRatio(ServerSignal x) => Ratio(Math.Abs(x.TakeProfit - x.EntryPrice), x.EntryPrice);
    private static double StopRatio(ServerSignal x) => Ratio(Math.Abs(x.StopLoss - x.EntryPrice), x.EntryPrice);
    private static double Ratio(decimal value, decimal basis) => basis <= 0m ? 0d : (double)(value / basis);
    private static double Closeness(double left, double right)
    {
        var scale = Math.Max(Math.Max(Math.Abs(left), Math.Abs(right)), 0.000001d);
        return 1d / (1d + Math.Abs(left - right) / (scale * 0.35d));
    }
}

public sealed record SignalSimilarityResult(decimal? TargetPercent, decimal? StopPercent,
    int SampleCount, int TargetSampleCount, int StopSampleCount);

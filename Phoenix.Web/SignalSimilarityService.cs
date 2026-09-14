namespace Phoenix.Web;

/// <summary>
/// Compares a new signal with completed target/stop signals. The result is a
/// historical similarity indicator, not a prediction or trading decision.
/// </summary>
public sealed class SignalSimilarityService(ServerOrderStore store)
{
    public async Task<SignalSimilarityResult> CalculateAsync(ServerSignal candidate,
        CancellationToken token = default)
    {
        var history = await store.GetHistoryAsync(3650, 5000, token);
        var samples = history.Select(x => x.Signal)
            .Where(x => x.Outcome is "Target" or "StopLoss")
            .Where(x => x.EntryPrice > 0m && x.Ceiling > x.Floor)
            .ToArray();

        var targetCount = samples.Count(x => x.Outcome == "Target");
        var stopCount = samples.Length - targetCount;
        if (targetCount == 0 || stopCount == 0)
            return new(null, null, samples.Length, targetCount, stopCount);

        var targetWeight = 0d;
        var stopWeight = 0d;
        foreach (var sample in samples)
        {
            // Raising the score makes the closest historical setups matter most.
            var weight = Math.Pow(Similarity(candidate, sample), 4);
            if (sample.Outcome == "Target") targetWeight += weight;
            else stopWeight += weight;
        }

        var total = targetWeight + stopWeight;
        if (total <= 0d) return new(null, null, samples.Length, targetCount, stopCount);
        var targetPercent = Math.Round((decimal)(100d * targetWeight / total), 1);
        return new(targetPercent, 100m - targetPercent, samples.Length, targetCount, stopCount);
    }

    internal static double Similarity(ServerSignal candidate, ServerSignal sample)
    {
        var weighted = 0d;
        var totalWeight = 0d;
        Add(candidate.Direction == sample.Direction ? 1d : 0d, 22d);
        Add(candidate.Symbol.Equals(sample.Symbol, StringComparison.OrdinalIgnoreCase) ? 1d : 0d, 18d);
        Add(Closeness(RangeRatio(candidate), RangeRatio(sample)), 22d);
        Add(Closeness(TargetRatio(candidate), TargetRatio(sample)), 16d);
        Add(Closeness(StopRatio(candidate), StopRatio(sample)), 12d);
        AddOptionalText(candidate.Timeframe, sample.Timeframe, 6d);
        AddOptionalText(candidate.ChartMode, sample.ChartMode, 4d);
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

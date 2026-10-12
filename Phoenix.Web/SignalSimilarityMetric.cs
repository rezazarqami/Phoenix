namespace Phoenix.Web;

/// <summary>Class-balanced historical dispersion, not a fitted outcome probability.
/// Constant readings carry no distance evidence. Each timeframe/window keeps its identity.</summary>
internal sealed class SignalSimilarityMetric
{
    private readonly Dictionary<string, double> scales;
    private SignalSimilarityMetric(Dictionary<string, double> scales) => this.scales = scales;

    internal static SignalSimilarityMetric? Build(IReadOnlyList<LearnedSignalPattern> patterns)
    {
        var groups = new[] { "Target", "StopLoss" }.Select(outcome => patterns
            .Where(x => x.Outcome == outcome).Select(x => Values(x.Signal, x.Features)).ToArray()).ToArray();
        if (groups.Any(x => x.Length < 5)) return null;
        var scales = new Dictionary<string, double>();
        foreach (var key in groups.SelectMany(x => x).SelectMany(x => x.Keys).Distinct())
        {
            var values = groups.Select(group => group.Where(x => x.ContainsKey(key)).Select(x => x[key]).ToArray()).ToArray();
            if (Enumerable.Range(0, 2).Any(i => values[i].Length < Math.Max(5, groups[i].Length / 2))) continue;
            // Equal class contribution prevents an abundant outcome from defining the ruler.
            var mean = values.Average(x => x.Average());
            var variance = values.Average(x => x.Average(v => (v - mean) * (v - mean)));
            if (variance > 0.000001d) scales[key] = Math.Max(0.05d, Math.Sqrt(variance));
        }
        return scales.Count == 0 ? null : new(scales);
    }

    internal double? Compare(ServerSignal candidate, TechnicalFeatureSnapshot features,
        LearnedSignalPattern sample)
    {
        var a = Values(candidate, features); var b = Values(sample.Signal, sample.Features);
        var shared = scales.Where(x => a.ContainsKey(x.Key) && b.ContainsKey(x.Key)).ToArray();
        if (shared.Length < Math.Max(1, (int)Math.Ceiling(scales.Count * 0.7d))) return null;
        // RMS distance in historical standard deviations: identical=100%, one SD=61%.
        // Missing measurements never become matching zeros or inflate resemblance.
        var distance = shared.Average(x => Math.Pow(Math.Clamp((a[x.Key] - b[x.Key]) / x.Value, -6d, 6d), 2));
        return Math.Exp(-0.5d * distance) *
            (SignalSimilarityService.TimeframeSimilarity(candidate.Timeframe, sample.Signal.Timeframe) ?? 1d);
    }

    private static Dictionary<string, double> Values(ServerSignal signal, TechnicalFeatureSnapshot features)
    {
        var result = new Dictionary<string, double>();
        var values = SignalOutcomeModel.PrimaryValues(signal, features);
        // Aggregated multi-scale readings would dilute a distinctive individual timeframe.
        for (var i = 0; i < 15; i++) if (values[i] is { } value) result[$"primary:{i}"] = value;
        var sign = signal.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? 1d : -1d;
        foreach (var level in features.MultiScaleLevels ?? [])
        {
            double[] readings = [(double)level.EntryStrength, (double)level.StopStrength,
                (double)level.TargetStrength, (double)level.FibonacciEntry,
                (double)level.FibonacciStop, (double)level.FibonacciTarget,
                (double)level.CloudEntry * sign, (double)level.CloudStop * sign,
                (double)level.CloudTarget * sign, (double)level.CloudBias * sign];
            for (var i = 0; i < readings.Length; i++) result[$"{level.Interval}:{level.Window}:{i}"] = readings[i];
        }
        return result;
    }
}

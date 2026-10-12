namespace Phoenix.Web;

/// <summary>Regularized outcome learning from entry-time evidence only. A later,
/// untouched time block must beat the historical target-rate baseline before use.</summary>
internal sealed class SignalOutcomeModel
{
    private readonly double[] means, scales, weights;
    private DateTime trainedThroughUtc;
    internal int ValidationCount { get; }
    internal double ValidationBrier { get; }
    internal double BaselineBrier { get; }
    private SignalOutcomeModel(double[] means, double[] scales, double[] weights,
        int count = 0, double score = 0, double baseline = 0)
    {
        this.means = means; this.scales = scales; this.weights = weights;
        ValidationCount = count; ValidationBrier = score; BaselineBrier = baseline;
    }

    internal static SignalOutcomeModel? Train(IReadOnlyList<LearnedSignalPattern> source,
        CancellationToken token = default)
    {
        // Unknown timestamps, open trades and duplicate records cannot train or validate.
        var rows = source.Where(x => x.Outcome is "Target" or "StopLoss")
            .Where(x => x.Signal.Id != Guid.Empty && x.Signal.EntryTriggeredAtUtc is not null &&
                x.Signal.CompletedAtUtc > x.Signal.EntryTriggeredAtUtc)
            .GroupBy(x => x.Signal.Id).Select(x => x.First())
            .OrderBy(x => x.Signal.EntryTriggeredAtUtc).ToArray();
        if (rows.Length < 60) return null;
        var split = rows.Length * 4 / 5;
        var cutoff = rows[split].Signal.EntryTriggeredAtUtc!.Value;
        var training = rows.Take(split).Where(x => x.Signal.CompletedAtUtc < cutoff).ToArray();
        var validation = rows.Skip(split).ToArray();
        if (!Enough(training) || !Enough(validation)) return null;
        var fitted = Fit(training, token);
        var prior = training.Count(x => x.Outcome == "Target") / (double)training.Length;
        double brier = 0, baseline = 0;
        foreach (var row in validation)
        {
            token.ThrowIfCancellationRequested();
            var prediction = fitted.Predict(row.Signal, row.Features);
            if (prediction is null) return null;
            var y = row.Outcome == "Target" ? 1d : 0d;
            brier += Math.Pow((double)prediction.Value / 100d - y, 2);
            baseline += Math.Pow(prior - y, 2);
        }
        brier /= validation.Length; baseline /= validation.Length;
        // Do not manufacture a decisive probability if the learned distinction
        // does not improve on simply using the previous outcome frequency.
        if (brier >= baseline * 0.95d) return null;
        // Keep the validated model fixed; do not refit using its holdout labels.
        return new(fitted.means, fitted.scales, fitted.weights, validation.Length, brier, baseline)
        { trainedThroughUtc = rows.Max(x => x.Signal.CompletedAtUtc!.Value) };
    }

    private static bool Enough(LearnedSignalPattern[] rows) =>
        rows.Count(x => x.Outcome == "Target") >= 5 && rows.Count(x => x.Outcome == "StopLoss") >= 5;

    private static SignalOutcomeModel Fit(LearnedSignalPattern[] rows, CancellationToken token)
    {
        var raw = rows.Select(x => Values(x.Signal, x.Features)).ToArray();
        var length = raw[0].Length;
        var means = new double[length]; var scales = new double[length];
        for (var j = 0; j < length; j++)
        {
            var values = raw.Select(x => x[j]).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            // Sparse legacy fields must not acquire weights from a handful of trades.
            if (values.Length < Math.Max(10, rows.Length / 2)) continue;
            means[j] = values.Average();
            scales[j] = Math.Sqrt(values.Average(x => Math.Pow(x - means[j], 2)));
            if (scales[j] < 0.0001d) scales[j] = 0;
        }
        var activeIndices = Enumerable.Range(0, length).Where(j => scales[j] > 0d).ToArray();
        var x = raw.Select(v => Standardize(v, means, scales)).ToArray();
        var weights = new double[length + 1];
        var prior = rows.Count(r => r.Outcome == "Target") / (double)rows.Length;
        weights[length] = Math.Log(prior / (1d - prior));
        for (var iteration = 0; iteration < 350; iteration++)
        {
            token.ThrowIfCancellationRequested();
            var gradient = new double[weights.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                var error = Logistic(Dot(x[i], weights)) - (rows[i].Outcome == "Target" ? 1d : 0d);
                foreach (var j in activeIndices) gradient[j] += error * x[i][j];
                gradient[length] += error;
            }
            for (var j = 0; j < weights.Length; j++)
                weights[j] -= 0.08d * (gradient[j] / rows.Length + (j < length ? 0.08d * weights[j] : 0d));
        }
        return new(means, scales, weights);
    }

    internal decimal? Predict(ServerSignal signal, TechnicalFeatureSnapshot features)
    {
        if (signal.EntryTriggeredAtUtc is { } entry && entry < trainedThroughUtc) return null;
        var values = Values(signal, features);
        var active = scales.Count(x => x > 0d);
        var observed = Enumerable.Range(0, scales.Length).Count(j => scales[j] > 0d && values[j].HasValue);
        if (active == 0 || observed < Math.Max(1, (int)Math.Ceiling(active * 0.7d))) return null;
        // Missing fields use the training mean, contributing zero evidence.
        var probability = Logistic(Dot(Standardize(values, means, scales), weights));
        return Math.Round((decimal)(probability * 100d), 0);
    }

    private static double[] Standardize(double?[] values, double[] means, double[] scales) =>
        Enumerable.Range(0, values.Length).Select(j => scales[j] > 0d && values[j].HasValue
            ? Math.Clamp((values[j]!.Value - means[j]) / scales[j], -4d, 4d) : 0d).ToArray();
    private static double Dot(double[] x, double[] w)
    {
        var value = w[^1];
        for (var j = 0; j < x.Length; j++) value += x[j] * w[j];
        return value;
    }
    private static double Logistic(double value) => 1d / (1d + Math.Exp(-Math.Clamp(value, -30d, 30d)));

    // Explicit allowlist: no RSI, volume, BTC alignment or reward/risk features.
    internal static double?[] PrimaryValues(ServerSignal s, TechnicalFeatureSnapshot f)
    {
        var sign = s.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
        var context = f.SimilarityContextVersion >= 1;
        double? D(decimal? value) => value.HasValue ? (double)value.Value : null;
        return [D((f.IchimokuEntryPosition ?? f.IchimokuPosition) * sign), D(f.IchimokuCloudBias * sign),
            D(sign > 0 ? f.SupportDistanceAtr : f.ResistanceDistanceAtr),
            D(sign > 0 ? f.ResistanceDistanceAtr : f.SupportDistanceAtr),
            D(f.EntryLevelStrength), D(f.StopLevelStrength), D(f.TargetLevelStrength),
            D(f.IchimokuStopPosition * sign), D(f.IchimokuTargetPosition * sign),
            f.AtrPercent > 0 ? Math.Log((double)f.AtrPercent) : null,
            context ? D(f.MarketRegimeScore) : null, context ? D(f.StructureScore) : null,
            context ? D(f.LiquiditySweepScore) : null, D(f.StructureBreakScore), D(f.StructureRetestScore)];
    }

    internal static double?[] Values(ServerSignal s, TechnicalFeatureSnapshot f)
    {
        var primary = PrimaryValues(s, f);
        var levels = f.MultiScaleLevels;
        var sign = s.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
        double? D(decimal? value) => value.HasValue ? (double)value.Value : null;
        // Stable slots preserve opposing readings on individual timeframes and windows.
        var multi = new List<double?>();
        foreach (var interval in new[] { "1", "5", "15", "60", "240", "D", "W", "M" })
        foreach (var window in new[] { 80, 240, 720 })
        {
            var level = levels?.FirstOrDefault(x => x.Interval == interval && x.Window == window);
            if (level is null) { multi.AddRange(new double?[10]); continue; }
            multi.AddRange([D(level.EntryStrength), D(level.StopStrength), D(level.TargetStrength),
                D(level.FibonacciEntry), D(level.FibonacciStop), D(level.FibonacciTarget),
                D(level.CloudEntry * sign), D(level.CloudStop * sign),
                D(level.CloudTarget * sign), D(level.CloudBias * sign)]);
        }
        return primary.Concat(multi).ToArray();
    }
}

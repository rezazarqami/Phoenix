namespace Phoenix.Web;

/// <summary>Regularized outcome learning from entry-time evidence only. A later,
/// untouched time block must beat the historical target-rate baseline before use.</summary>
internal sealed class SignalOutcomeModel
{
    private readonly double[] means, scales, weights;
    private DateTime trainedThroughUtc;
    internal string EvidenceStage { get; private set; } = "entry";
    private double calibrationSlope = 1d, calibrationIntercept;
    internal int CalibrationCount { get; private set; }
    internal double ValidationLogLoss { get; private set; }
    internal double BaselineLogLoss { get; private set; }
    internal IReadOnlyList<OutcomeReliabilityBin> Reliability { get; private set; } = [];
    internal DateTime AvailableAfterUtc => trainedThroughUtc;
    internal int ValidationCount { get; private set; }
    internal double ValidationBrier { get; private set; }
    internal double BaselineBrier { get; private set; }
    private SignalOutcomeModel(double[] means, double[] scales, double[] weights)
    {
        this.means = means; this.scales = scales; this.weights = weights;
    }

    internal static SignalOutcomeModel? Train(IReadOnlyList<LearnedSignalPattern> source,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (source.Select(x => x.EvidenceStage).Distinct().Count() > 1) return null;
        // Unknown timestamps, open trades and duplicate records cannot train or validate.
        var rows = SignalPatternIdentity.Unique(source.Where(x => x.Outcome is "Target" or "StopLoss")
            .Where(x => x.Signal.Id != Guid.Empty && x.ObservedAtUtc is not null &&
                x.Signal.CompletedAtUtc > x.ObservedAtUtc))
            .OrderBy(x => x.ObservedAtUtc).ToArray();
        if (rows.Length < 100 || rows.Select(x => x.EvidenceStage).Distinct().Count() != 1) return null;
        // Three chronological blocks: fit / calibration / untouched audit.
        // Purge labels that would still be unknown at the following boundary.
        var calibrationStart = rows[rows.Length * 3 / 5].ObservedAtUtc!.Value;
        var auditStart = rows[rows.Length * 4 / 5].ObservedAtUtc!.Value;
        var training = rows.Where(x => x.ObservedAtUtc < calibrationStart &&
            x.Signal.CompletedAtUtc < calibrationStart).ToArray();
        var calibration = rows.Where(x => x.ObservedAtUtc >= calibrationStart &&
            x.ObservedAtUtc < auditStart && x.Signal.CompletedAtUtc < auditStart).ToArray();
        var validation = rows.Where(x => x.ObservedAtUtc >= auditStart).ToArray();
        if (!Enough(training) || !Enough(calibration) || !Enough(validation) ||
            calibration.Length < 20 || validation.Length < 20) return null;
        var fitted = Fit(training, token);
        var calibrationScores = calibration.Select(x => fitted.RawScore(x.Signal, x.Features)).ToArray();
        if (calibrationScores.Any(x => x is null)) return null;
        (fitted.calibrationSlope, fitted.calibrationIntercept) = OutcomeCalibration.Fit(
            calibrationScores.Select(x => x!.Value).ToArray(),
            calibration.Select(x => x.Outcome == "Target").ToArray(), token);
        var prior = training.Concat(calibration).Count(x => x.Outcome == "Target") /
            (double)(training.Length + calibration.Length);
        var predictions = new List<(double Probability, bool Target)>();
        foreach (var row in validation)
        {
            token.ThrowIfCancellationRequested();
            var score = fitted.RawScore(row.Signal, row.Features);
            if (score is null) return null;
            predictions.Add((Logistic(fitted.calibrationSlope * score.Value +
                fitted.calibrationIntercept), row.Outcome == "Target"));
        }
        double Brier((double Probability, bool Target) x) => Math.Pow(x.Probability - (x.Target ? 1d : 0d), 2);
        double BaseBrier((double Probability, bool Target) x) => Math.Pow(prior - (x.Target ? 1d : 0d), 2);
        var brier = predictions.Average(Brier);
        var baseline = predictions.Average(BaseBrier);
        var logLoss = predictions.Average(x => OutcomeCalibration.LogLoss(x.Probability, x.Target));
        var baselineLogLoss = predictions.Average(x => OutcomeCalibration.LogLoss(prior, x.Target));
        // Score unrounded probabilities; one lucky older half must not hide recent failure.
        var recent = predictions.Skip(predictions.Count / 2).ToArray();
        if (brier >= baseline * 0.95d || logLoss >= baselineLogLoss ||
            recent.Average(Brier) >= recent.Average(BaseBrier)) return null;
        // Never refit on the audit labels or use them to choose the calibrator.
        fitted.EvidenceStage = rows[0].EvidenceStage;
        fitted.CalibrationCount = calibration.Length;
        fitted.ValidationCount = validation.Length;
        fitted.ValidationBrier = brier; fitted.BaselineBrier = baseline;
        fitted.ValidationLogLoss = logLoss; fitted.BaselineLogLoss = baselineLogLoss;
        fitted.Reliability = OutcomeCalibration.Bins(predictions);
        fitted.trainedThroughUtc = rows.Max(x => x.Signal.CompletedAtUtc!.Value);
        return fitted;
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
        // Bound the Hessian trace so correlated multi-timeframe fields cannot
        // make a fixed learning rate oscillate as the feature count grows.
        var step = Math.Min(0.08d, 1d / (0.25d * x.Average(v => 1d + v.Sum(z => z * z)) + 0.08d));
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
                weights[j] -= step * (gradient[j] / rows.Length + (j < length ? 0.08d * weights[j] : 0d));
        }
        return new(means, scales, weights);
    }

    internal decimal? Predict(ServerSignal signal, TechnicalFeatureSnapshot features,
        string evidenceStage = "entry", DateTime? observedAtUtc = null)
    {
        if (evidenceStage != EvidenceStage) return null;
        var evidenceTime = observedAtUtc ?? signal.EntryTriggeredAtUtc ??
            (signal.CreatedAtUtc == default ? DateTime.UtcNow : signal.CreatedAtUtc);
        if (evidenceTime <= trainedThroughUtc) return null;
        var score = RawScore(signal, features);
        return score is null ? null : Math.Round((decimal)(100d * Logistic(
            calibrationSlope * score.Value + calibrationIntercept)), 0);
    }

    private double? RawScore(ServerSignal signal, TechnicalFeatureSnapshot features)
    {
        var values = Values(signal, features);
        var active = scales.Count(x => x > 0d);
        var observed = Enumerable.Range(0, scales.Length).Count(j => scales[j] > 0d && values[j].HasValue);
        if (active == 0 || observed < Math.Max(1, (int)Math.Ceiling(active * 0.7d))) return null;
        // Missing fields use the training mean, contributing zero evidence.
        return Dot(Standardize(values, means, scales), weights);
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

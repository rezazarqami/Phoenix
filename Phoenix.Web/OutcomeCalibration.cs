namespace Phoenix.Web;

public sealed record OutcomeReliabilityBin(int LowerPercent, int UpperPercent, int Count,
    double MeanPrediction, double TargetRate);

/// <summary>Monotone sigmoid calibration on a separate, past-only block.</summary>
internal static class OutcomeCalibration
{
    internal static (double Slope, double Intercept) Fit(double[] scores, bool[] targets,
        CancellationToken token = default)
    {
        if (scores.Length != targets.Length || scores.Length == 0 ||
            scores.Any(x => !double.IsFinite(x))) throw new ArgumentException("Invalid calibration data.");
        var positives = targets.Count(x => x);
        // Smoothed targets prevent separation from generating infinite confidence.
        var positive = (positives + 1d) / (positives + 2d);
        var negative = 1d / (targets.Length - positives + 2d);
        double slope = 1d, intercept = 0d;
        var bounded = scores.Select(x => Math.Clamp(x, -20d, 20d)).ToArray();
        // Lipschitz bound for a stable two-parameter gradient step.
        var step = 1d / (0.25d * bounded.Average(x => x * x + 1d) + 0.02d);
        for (var iteration = 0; iteration < 600; iteration++)
        {
            token.ThrowIfCancellationRequested();
            double slopeGradient = 0, interceptGradient = 0;
            for (var i = 0; i < bounded.Length; i++)
            {
                var p = 1d / (1d + Math.Exp(-Math.Clamp(slope * bounded[i] + intercept, -30d, 30d)));
                var error = p - (targets[i] ? positive : negative);
                slopeGradient += error * bounded[i]; interceptGradient += error;
            }
            // Shrink toward identity; never reverse the learned ranking to rescue a bad model.
            slope = Math.Clamp(slope - step * (slopeGradient / scores.Length + 0.01d * (slope - 1d)), 0d, 10d);
            intercept = Math.Clamp(intercept - step * (interceptGradient / scores.Length + 0.01d * intercept), -10d, 10d);
        }
        return (slope, intercept);
    }

    internal static double LogLoss(double p, bool target)
    {
        p = Math.Clamp(p, 1e-12d, 1d - 1e-12d);
        return -Math.Log(target ? p : 1d - p);
    }

    internal static IReadOnlyList<OutcomeReliabilityBin> Bins(
        IEnumerable<(double Probability, bool Target)> predictions) => predictions
        .GroupBy(x => Math.Min(9, (int)(x.Probability * 10d))).OrderBy(x => x.Key)
        .Select(g => new OutcomeReliabilityBin(g.Key * 10, (g.Key + 1) * 10, g.Count(),
            g.Average(x => x.Probability), g.Count(x => x.Target) / (double)g.Count())).ToArray();
}

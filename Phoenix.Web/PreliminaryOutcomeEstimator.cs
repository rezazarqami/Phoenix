namespace Phoenix.Web;

/// <summary>Versioned, provisional outcome frequency, NOT a calibrated forecast.
/// Same observation stage and timeframe only; never normalizes the two class
/// resemblance scores. Fixed settings are not tuned on the evaluation labels.</summary>
internal static class PreliminaryOutcomeEstimator
{
    internal const int MinimumSamples = 10;
    internal const string Version = "technical-neighbours-v1";

    internal static OutcomeEstimateEvidence Estimate(ServerSignal candidate,
        IReadOnlyList<LearnedSignalPattern> source, string stage, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var at = stage == "entry" ? candidate.EntryTriggeredAtUtc : candidate.ProposalEvidence?.ObservedAtUtc ?? candidate.CreatedAtUtc;
        if (candidate.TechnicalFeatures is null || at is null || at == default(DateTime) ||
            string.IsNullOrWhiteSpace(candidate.Timeframe)) return Missing("missing-query-evidence");
        var rows = SignalPatternIdentity.Unique(source.Where(x => x.EvidenceStage == stage &&
            x.Signal.Id != Guid.Empty && x.Signal.Id != candidate.Id &&
            x.ObservedAtUtc is not null && x.ObservedAtUtc < at &&
            x.Signal.CompletedAtUtc > x.ObservedAtUtc && x.Signal.CompletedAtUtc < at &&
            x.Signal.Timeframe == candidate.Timeframe &&
            (stage != "proposal" || x.Features.SimilarityContextVersion >= 3)));
        // Deduplicate ALL outcomes before selecting binary resolutions: a conflict
        // with a risk-free record must not quietly turn into a target/stop example.
        rows = rows.Where(x => x.Outcome is "Target" or "StopLoss").ToArray();
        if (rows.Length < MinimumSamples || rows.Count(x => x.Outcome == "Target") < 2 ||
            rows.Count(x => x.Outcome == "StopLoss") < 2) return Missing("insufficient-stage-timeframe-outcomes", rows.Length);
        var metric = SignalSimilarityMetric.Build(rows);
        double weights = 0, targetWeights = 0, squaredWeights = 0;
        var nearby = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            var similarity = metric is null
                ? SignalSimilarityService.TechnicalSimilarity(candidate, row.Signal,
                    candidate.TechnicalFeatures, row.Features)
                : metric.Compare(candidate, candidate.TechnicalFeatures, row);
            if (similarity is null || similarity < (metric is null ? .65d : .5d)) continue;
            var w = Math.Exp(-8d * (1d - similarity.Value));
            weights += w; squaredWeights += w * w;
            if (row.Outcome == "Target") targetWeights += w;
            nearby++;
        }
        var effective = squaredWeights > 0 ? weights * weights / squaredWeights : 0;
        if (nearby < 5 || effective < 5d - 1e-9) return Missing("insufficient-nearby-evidence", rows.Length, nearby, effective);
        // Laplace-smoothed same-stage/timeframe prior; ten equivalent observations
        // shrink small neighbourhoods. Uneven weights reduce effective support.
        // Correlated trades can still overstate the independent evidence.
        var prior = (rows.Count(x => x.Outcome == "Target") + 1d) / (rows.Length + 2d);
        var probability = (effective * targetWeights / weights + 10d * prior) / (effective + 10d);
        return new(Version, "preliminary", stage, rows.Length, nearby, (decimal)effective,
            Math.Round((decimal)(100d * probability), 0));

        OutcomeEstimateEvidence Missing(string reason, int count = 0, int close = 0, double n = 0) =>
            new(Version, reason, stage, count, close, (decimal)n, null);
    }
}

// Stored inside frozen ProposalEvidence.Prediction for prospective scoring.
// Diagnostics are available to research tools, never added to the review message.
public sealed record OutcomeEstimateEvidence(string Version, string Status, string Stage,
    int SampleCount, int NearbyCount, decimal EffectiveCount, decimal? TargetPercent);

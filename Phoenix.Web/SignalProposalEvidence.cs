namespace Phoenix.Web;

/// <summary>Frozen evidence actually observed before approval. Legacy evidence stays unknown.</summary>
public sealed record SignalProposalEvidence(
    DateTime ObservedAtUtc, string Symbol, string Direction, string Timeframe,
    decimal EntryPrice, decimal TakeProfit, decimal StopLoss,
    TechnicalFeatureSnapshot Features, SignalSimilarityResult Prediction)
{
    public OutcomeEvaluationPolicy? EvaluationPolicy { get; init; }
    internal static SignalProposalEvidence? Capture(SignalCandidate candidate, string timeframe,
        ProfessionalSignalAnalysis analysis) => analysis.ObservedAtUtc is { } observed
        ? new(observed, candidate.Symbol, candidate.Direction, timeframe,
            candidate.EntryPrice, candidate.TakeProfit, candidate.StopLoss,
            analysis.Features with { MultiScaleLevels = analysis.Features.MultiScaleLevels?.ToArray() },
            analysis.Prediction) { EvaluationPolicy = OutcomeWindows.Horizon(timeframe) is { } minutes
                ? new(1,24,minutes,"proposal","recorded-first-event; simultaneous=unknown") : null } : null;
}

public sealed record OutcomeEvaluationPolicy(int Version, int Bars, int HorizonMinutes,
    string ObservationStage, string EventRule);

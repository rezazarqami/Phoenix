namespace Phoenix.Web;

/// <summary>Descriptive historical support, never a calibrated probability.</summary>
public sealed record SimilarityEvidence(string QueryStage, string ReferenceStage,
    int ComparableTargets, int ComparableStops, int NearbyTargets, int NearbyStops,
    decimal EffectiveTargets, decimal EffectiveStops, int SameTimeframeNearbyTargets,
    int SameTimeframeNearbyStops)
{
    public int SameTimeframeComparable { get; init; }
    public int OtherTimeframeComparable { get; init; }
    public int ArchivedCompletedCount { get; init; }
    public int MissingStageSnapshotCount { get; init; }
    public int EvaluatedSnapshotCount { get; init; }
    public DateTime? ReferenceRefreshedAtUtc { get; init; }
    public int ComparisonVersion { get; init; }
    public string? Metric { get; init; }
    public int InsufficientFeatureCount { get; init; }
    public IReadOnlyList<SimilarityExample>? NearestExamples { get; init; }
    public int ComparableRiskFree { get; init; }
    public int SameTimeframeNearbyRiskFree { get; init; }
    public int OtherTimeframeNearbyTargets { get; init; }
    public int OtherTimeframeNearbyStops { get; init; }
    public int OtherTimeframeNearbyRiskFree { get; init; }
    public TechnicalCohortEvidence? TechnicalCohort { get; init; }
    public OutcomeWindowEvidence? MainOutcomeWindow { get; init; }
    public OutcomeWindowEvidence? ObservationOutcomeWindow { get; init; }
    public TradePayoffEvidence? Payoff { get; init; }
    public int SameTimeframeNearbyCount => SameTimeframeNearbyTargets + SameTimeframeNearbyStops;
    public decimal? HistoricalTargetRate => SameTimeframeNearbyCount < 10 ? null
        : Math.Round(100m * SameTimeframeNearbyTargets / SameTimeframeNearbyCount, 1);
}

public sealed record SimilarityExample(Guid SignalId,string Outcome,string? Timeframe,decimal Score,
    bool Nearby,IReadOnlyDictionary<string,decimal> GroupDistances);

public sealed record TradePayoffEvidence(decimal RewardRisk, decimal GrossBinaryBreakEvenRate)
{
    internal static TradePayoffEvidence? From(ServerSignal s)
    {
        if(s.EntryPrice<=0 || s.TakeProfit<=0 || s.StopLoss<=0)return null;
        var sign=s.Direction.Equals("Long",StringComparison.OrdinalIgnoreCase)?1m:
            s.Direction.Equals("Short",StringComparison.OrdinalIgnoreCase)?-1m:0m;
        var risk=(s.EntryPrice-s.StopLoss)*sign;var reward=(s.TakeProfit-s.EntryPrice)*sign;
        return risk>0 && reward>0 ? new(Math.Round(reward/risk,3),Math.Round(100m*risk/(risk+reward),1)):null;
    }
}

internal static class SignalPatternIdentity
{
    internal static LearnedSignalPattern[] Unique(IEnumerable<LearnedSignalPattern> patterns) => patterns
        .Select((p,i) => (Pattern:p, Key:p.Signal.Id == Guid.Empty ? $"legacy:{i}" : p.Signal.Id.ToString()))
        .GroupBy(x => x.Key).Where(g => g.Select(x => x.Pattern.Outcome).Distinct().Count() == 1)
        .Select(g => g.First().Pattern)
        .Select((p,i) => (Pattern:p, Key:Episode(p,i)))
        .GroupBy(x => x.Key).Where(g => g.Select(x => x.Pattern.Outcome).Distinct().Count() == 1)
        .Select(g => g.First().Pattern).ToArray();

    private static string Episode(LearnedSignalPattern p, int index)
    {
        var s=p.Signal;
        if (string.IsNullOrWhiteSpace(s.Symbol) || p.ObservedAtUtc is null || s.EntryPrice <= 0 ||
            s.TakeProfit <= 0 || s.StopLoss <= 0) return $"row:{index}";
        return FormattableString.Invariant($"{p.EvidenceStage}|{s.Symbol.ToUpperInvariant()}|{s.Direction}|{s.Timeframe}|{s.EntryPrice}|{s.TakeProfit}|{s.StopLoss}|{p.ObservedAtUtc.Value.Ticks / TimeSpan.TicksPerMinute}");
    }
}

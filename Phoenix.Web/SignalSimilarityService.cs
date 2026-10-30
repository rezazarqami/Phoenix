namespace Phoenix.Web;

/// <summary>Independent resemblance to completed target and stop examples.
/// Scores are not probabilities and need not sum to 100.</summary>
public sealed class SignalSimilarityService(SignalLearningService learning)
{
    public async Task<SignalSimilarityResult> CalculateAsync(ServerSignal candidate,
        CancellationToken token = default)
    {
        if (candidate.TechnicalFeatures is null) return new(null, null, 0, 0, 0);
        var snapshot = await learning.GetSnapshotAsync(token);
        var atEntry = candidate.EntryTriggeredAtUtc is not null;
        // Prefer the same observation stage, even for small or single-outcome
        // cohorts. Legacy entry fallback is explicitly labeled in the summary.
        var patterns = !atEntry && snapshot.ProposalPatterns.Count > 0
            ? snapshot.ProposalPatterns : snapshot.Patterns;
        var result = CalculateFromPatterns(candidate, patterns, token);
        var cutoff = candidate.EntryTriggeredAtUtc ?? candidate.CreatedAtUtc;
        var archived = snapshot.OutcomeHistory.Select(x => x.Item.Signal)
            .Where(x => (x.Id != candidate.Id || candidate.Id == Guid.Empty) && x.Outcome is "Target" or "StopLoss" or "RiskFree")
            .Where(x => x.CompletedAtUtc is not null && (cutoff == default || x.CompletedAtUtc < cutoff))
            .Select(x => x.Id).Distinct().Count();
        return result with { Evidence = result.Evidence! with {
            ArchivedCompletedCount = archived,
            MissingStageSnapshotCount = Math.Max(0, archived - result.SampleCount),
            ReferenceRefreshedAtUtc = snapshot.RefreshedAtUtc
        }};
    }

    internal static SignalSimilarityResult CalculateFromPatterns(ServerSignal candidate,
        IReadOnlyList<LearnedSignalPattern> patterns, CancellationToken token = default)
    {
        if (candidate.TechnicalFeatures is null) return new(null, null, 0, 0, 0);
        var cutoff = candidate.EntryTriggeredAtUtc ?? (candidate.CreatedAtUtc == default ? (DateTime?)null : candidate.CreatedAtUtc);
        patterns = SignalPatternIdentity.Unique(patterns.Where(x => x.Signal.Id != candidate.Id || candidate.Id == Guid.Empty)
            .Where(x => cutoff is null || x.Signal.CompletedAtUtc < cutoff));
        // Evaluate each historical signal once, before splitting by outcome/timeframe.
        // No minimum counts, class quotas, nearest-N truncation or positive score floor.
        var compared=patterns.Select(p => {
            token.ThrowIfCancellationRequested();
            return (Pattern:p, Match:TechnicalResemblance.Compare(candidate,p.Signal,
                candidate.TechnicalFeatures,p.Features));
        }).ToArray();
        var usable=compared.Where(x=>x.Match.Comparable).ToArray();
        var nearby=usable.Where(x=>x.Match.Nearby).ToArray();
        bool Same(LearnedSignalPattern p)=>!string.IsNullOrWhiteSpace(candidate.Timeframe) &&
            candidate.Timeframe==p.Signal.Timeframe;
        int Used(string outcome)=>usable.Count(x=>x.Pattern.Outcome==outcome);
        int Close(string outcome,bool same)=>nearby.Count(x=>x.Pattern.Outcome==outcome &&
            !string.IsNullOrWhiteSpace(x.Pattern.Signal.Timeframe) &&
            !string.IsNullOrWhiteSpace(candidate.Timeframe) && Same(x.Pattern)==same);
        decimal? Score(string outcome)
        {
            var matches=usable.Where(x=>x.Pattern.Outcome==outcome).Select(x=>x.Match.Score).ToArray();
            return matches.Length<5?null:Math.Round((decimal)matches.Average()*100m,1);
        }
        var support=new SimilarityEvidence(candidate.EntryTriggeredAtUtc is null?"proposal":"entry",
            patterns.All(x=>x.EvidenceStage=="proposal")?"proposal":"entry",
            Used("Target"),Used("StopLoss"),nearby.Count(x=>x.Pattern.Outcome=="Target"),
            nearby.Count(x=>x.Pattern.Outcome=="StopLoss"),Used("Target"),Used("StopLoss"),
            Close("Target",true),Close("StopLoss",true))
        {
            SameTimeframeComparable=usable.Count(x=>Same(x.Pattern)),
            OtherTimeframeComparable=usable.Count(x=>!Same(x.Pattern) && !string.IsNullOrWhiteSpace(x.Pattern.Signal.Timeframe)),
            ComparisonVersion=TechnicalResemblance.Version,Metric=TechnicalResemblance.Metric,
            ComparableRiskFree=Used("RiskFree"),
            InsufficientFeatureCount=compared.Length-usable.Length,
            EvaluatedSnapshotCount=compared.Length,
            SameTimeframeNearbyRiskFree=Close("RiskFree",true),
            OtherTimeframeNearbyTargets=Close("Target",false),
            OtherTimeframeNearbyStops=Close("StopLoss",false),
            OtherTimeframeNearbyRiskFree=Close("RiskFree",false),
            // Bounded audit only; every comparable row contributes to the counts above.
            NearestExamples=usable.OrderByDescending(x=>x.Match.Score).Take(12).Select(x=>
                new SimilarityExample(x.Pattern.Signal.Id,x.Pattern.Outcome,x.Pattern.Signal.Timeframe,
                    Math.Round((decimal)x.Match.Score*100m,2),x.Match.Nearby,
                    x.Match.Distances.ToDictionary(g=>g.Key,g=>Math.Round((decimal)g.Value,3)))).ToArray()
        };
        return new(Score("Target"),Score("StopLoss"),patterns.Count,
            patterns.Count(x=>x.Outcome=="Target"),patterns.Count(x=>x.Outcome=="StopLoss"),
            Used("Target")+Used("StopLoss")){Evidence=support};
    }

    internal static double TechnicalSimilarity(ServerSignal candidate,ServerSignal sample,
        TechnicalFeatureSnapshot candidateFeatures,TechnicalFeatureSnapshot sampleFeatures,bool matchTimeframe=true)
    {
        var comparison=TechnicalResemblance.Compare(candidate,sample,candidateFeatures,sampleFeatures);
        return comparison.Score; // Timeframe only partitions the displayed counts.
    }

    internal static double? TimeframeSimilarity(string? left, string? right)
    {
        static double Minutes(string? interval) => interval switch
        {
            "D" => 1440d, "W" => 10080d, "M" => 43200d,
            _ when double.TryParse(interval, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0d => value,
            _ => 0d
        };
        var a = Minutes(left);
        var b = Minutes(right);
        return a <= 0d || b <= 0d ? null : Math.Clamp(1d - Math.Abs(Math.Log(a / b)) / Math.Log(16d), 0d, 1d);
    }


}

public sealed record SignalSimilarityResult(decimal? TargetPercent, decimal? StopPercent,
    int SampleCount, int TargetSampleCount, int StopSampleCount, int CalibrationSampleCount = 0,
    decimal? TargetOutcomePercent = null, decimal? StopOutcomePercent = null,
    int OutcomeValidationCount = 0)
{
    public SimilarityEvidence? Evidence { get; init; }
}

namespace Phoenix.Web;

public sealed record OutcomeWindowEvidence(string Source, string Stage, int HorizonMinutes,
    int Targets, int Stops, int RiskFree, int Expired, int OtherExit,
    int NoRecordedResolution, int Unknown, int Immature)
{
    public int LegacyObservationCount { get; init; }
    public int MatureCount => Targets+Stops+RiskFree+Expired+OtherExit+NoRecordedResolution+Unknown;
    public decimal? RecordedTargetShare => MatureCount<10 || LegacyObservationCount>0 ? null : Math.Round(100m*Targets/MatureCount,1);
    public decimal? RecordedStopShare => MatureCount<10 || LegacyObservationCount>0 ? null : Math.Round(100m*Stops/MatureCount,1);
}

internal static class OutcomeWindows
{
    internal static int? Horizon(string? timeframe) => timeframe switch
    {
        "1"=>24,"5"=>120,"15"=>360,"60"=>1440,"240"=>5760,_=>null
    };

    // These are recorded operational outcomes, not reconstructed OHLC first touches.
    internal static string Classify(SignalHistoryItem item, DateTime observed, DateTime deadline, DateTime asOf)
    {
        var s=item.Signal;
        if(deadline>asOf)return "Immature";
        if(s.RecoveryOriginalStatus is not null || s.Status=="RecoveryRequired" || item.UpdatedAtUtc>asOf ||
            item.UpdatedAtUtc<observed || s.CompletedAtUtc<observed)return "Unknown";
        var events=new List<(string Label,DateTime At)>();
        void Add(string label,DateTime? value)
        {
            if(value is { } at && at>=observed && at<=deadline)events.Add((label,at));
        }
        Add("Target",s.TargetReachedAtUtc);Add("StopLoss",s.StopLossReachedAtUtc);
        Add("RiskFree",s.RiskFreeClosedAtUtc);Add("Expired",s.ExpiredAtUtc);
        if(events.Count>0)
        {
            var first=events.Min(x=>x.At);var earliest=events.Where(x=>x.At==first).ToArray();
            if(earliest.Length!=1)return "Unknown";
            var label=earliest[0].Label;
            if(label is "Target" or "StopLoss" or "RiskFree")
                if(s.EntryTriggeredAtUtc is not { } entry || entry<observed || entry>first)return "Unknown";
            return label;
        }
        if(s.CompletedAtUtc is { } end && end<=deadline)
            return s.Status is "Cancelled" or "Rejected" or "Deactivated" ? "OtherExit":"Unknown";
        // Only an archive observation at/after the horizon supports this category.
        // It means no recorded resolution, not proof of continuous market coverage.
        return item.UpdatedAtUtc>=deadline && (item.RemovedAtUtc is null || item.RemovedAtUtc>=deadline)
            ? "NoRecordedResolution":"Unknown";
    }

    internal static OutcomeWindowEvidence[] Calculate(ServerSignal candidate,
        IReadOnlyList<(string Source, SignalHistoryItem Item)> history, string stage)
    {
        var horizon=Horizon(candidate.Timeframe);
        var asOf=candidate.EntryTriggeredAtUtc ?? candidate.CreatedAtUtc;
        if(horizon is null || asOf==default || candidate.TechnicalFeatures is null)return [];
        var output=new List<OutcomeWindowEvidence>();
        foreach(var source in new[]{"main","observation"})
        {
            var legacyKeys=new HashSet<string>();
            var counts=new Dictionary<string,int>();var episodes=new Dictionary<string,List<string>>();
            foreach(var row in history.Where(x=>x.Source==source).OrderBy(x=>x.Item.Signal.CreatedAtUtc))
            {
                var s=row.Item.Signal;
                var p=s.ProposalEvidence;
                if(stage=="proposal" && p?.EvaluationPolicy is { } policy &&
                    (policy.Version!=1 || policy.HorizonMinutes!=horizon || policy.ObservationStage!="proposal"))continue;
                var observed=stage=="proposal" ? p?.ObservedAtUtc:s.EntryTriggeredAtUtc;
                var f=stage=="proposal" ? p?.Features:s.EntryTechnicalFeatures;
                if(observed is null || observed>=asOf || f is null || (stage=="proposal" && f.SimilarityContextVersion<3))continue;
                var reference=stage=="proposal" ? new ServerSignal { Symbol=p!.Symbol,Direction=p.Direction,Timeframe=p.Timeframe,
                    EntryPrice=p.EntryPrice,TakeProfit=p.TakeProfit,StopLoss=p.StopLoss }:s;
                if(s.Id==candidate.Id || reference.Timeframe!=candidate.Timeframe ||
                    !reference.Direction.Equals(candidate.Direction,StringComparison.OrdinalIgnoreCase) ||
                    !TechnicalCohorts.SimilarGeometry(candidate,reference))continue;
                // Fixed technical ruler independent of all outcome labels, including unresolved rows.
                if(SignalSimilarityService.TechnicalSimilarity(candidate,reference,candidate.TechnicalFeatures,f)<.65)continue;
                var key=FormattableString.Invariant($"{reference.Symbol.ToUpperInvariant()}|{reference.Direction}|{reference.Timeframe}|{reference.EntryPrice}|{reference.TakeProfit}|{reference.StopLoss}|{observed.Value.Ticks/TimeSpan.TicksPerMinute}");
                var label=Classify(row.Item,observed.Value,observed.Value.AddMinutes(horizon.Value),asOf);
                if(source=="observation" && s.ObservationPolicyVersion is null)legacyKeys.Add(key);
                if(!episodes.TryGetValue(key,out var labels))episodes[key]=labels=[];
                labels.Add(label);
            }
            foreach(var labels in episodes.Values)
            {
                var label=labels.Distinct().Count()==1?labels[0]:"Unknown";
                counts[label]=counts.GetValueOrDefault(label)+1;
            }
            int N(string name)=>counts.GetValueOrDefault(name);
            output.Add(new(source,stage,horizon.Value,N("Target"),N("StopLoss"),N("RiskFree"),N("Expired"),
                N("OtherExit"),N("NoRecordedResolution"),N("Unknown"),N("Immature")) { LegacyObservationCount=legacyKeys.Count });
        }
        return output.ToArray();
    }
}

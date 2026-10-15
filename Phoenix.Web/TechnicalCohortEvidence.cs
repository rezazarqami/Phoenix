namespace Phoenix.Web;

/// <summary>Descriptive cohorts with fixed, outcome-independent membership rules.</summary>
public sealed record TechnicalConditionEvidence(string Label, int Targets, int Stops);
public sealed record TechnicalCohortEvidence(int Targets, int Stops, int BaselineTargets,
    int BaselineStops, int DistinctSymbols, int DistinctDays, decimal EffectiveSamples,
    int RecentTargets, int RecentStops, DateTime? FirstObservedUtc, DateTime? LastObservedUtc,
    TechnicalConditionEvidence Trend, TechnicalConditionEvidence Volume,
    TechnicalConditionEvidence? Structure)
{
    public IReadOnlyList<TechnicalConditionEvidence> Conditions => Structure is null
        ? [Trend, Volume] : [Trend, Volume, Structure];
    public int Count => Targets + Stops;
    public decimal? TargetRate => Count < 10 ? null : Math.Round(100m * Targets / Count, 1);
    public decimal? BaselineTargetRate => BaselineTargets + BaselineStops < 10 ? null
        : Math.Round(100m * BaselineTargets / (BaselineTargets + BaselineStops), 1);
}

internal static class TechnicalCohorts
{
    internal static TechnicalCohortEvidence Build(ServerSignal candidate,
        IReadOnlyList<LearnedSignalPattern> patterns,
        IReadOnlyList<(LearnedSignalPattern Pattern, double Weight)> nearby)
    {
        bool Same(LearnedSignalPattern p) => !string.IsNullOrWhiteSpace(candidate.Timeframe) &&
            candidate.Timeframe == p.Signal.Timeframe &&
            candidate.Direction.Equals(p.Signal.Direction, StringComparison.OrdinalIgnoreCase);
        var baseline = patterns.Where(Same).ToArray();
        var selected = nearby.Where(x => Same(x.Pattern) && SimilarGeometry(candidate,x.Pattern.Signal)).ToArray();
        var rows = selected.Select(x => x.Pattern).ToArray();
        var dates = rows.Where(x => x.ObservedAtUtc.HasValue).Select(x => x.ObservedAtUtc!.Value).ToArray();
        var cutoff = candidate.EntryTriggeredAtUtc ?? candidate.CreatedAtUtc;
        var recent = cutoff == default ? [] : rows.Where(x => x.ObservedAtUtc >= cutoff.AddDays(-30)).ToArray();
        var conditions = new List<TechnicalConditionEvidence>();
        var f = candidate.TechnicalFeatures!;
        Add("روند هم‌وضعیت", p => Bucket(p.Features.TrendStrength * Sign(p.Signal), .1m) == Bucket(f.TrendStrength * Sign(candidate), .1m));
        Add("حجم نسبی هم‌وضعیت", p => Volume(p.Features.VolumeRatio) == Volume(f.VolumeRatio));
        if (f.SimilarityContextVersion >= 1)
            Add("ساختار هم‌وضعیت", p => p.Features.SimilarityContextVersion >= 1 && Bucket(p.Features.StructureScore-.5m,.2m) == Bucket(f.StructureScore-.5m,.2m));
        var sum = selected.Sum(x=>x.Weight); var squares=selected.Sum(x=>x.Weight*x.Weight);
        return new(Count(rows,"Target"),Count(rows,"StopLoss"),Count(baseline,"Target"),Count(baseline,"StopLoss"),
            rows.Select(x=>x.Signal.Symbol).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            dates.Select(x=>x.Date).Distinct().Count(),squares>0 ? Math.Round((decimal)(sum*sum/squares),1):0,
            Count(recent,"Target"),Count(recent,"StopLoss"),dates.Length==0?null:dates.Min(),dates.Length==0?null:dates.Max(),
            conditions[0],conditions[1],conditions.Count>2?conditions[2]:null);
        void Add(string label,Func<LearnedSignalPattern,bool> predicate)
        {
            var subset=rows.Where(predicate).ToArray();
            conditions.Add(new(label,Count(subset,"Target"),Count(subset,"StopLoss")));
        }
    }
    private static int Count(IEnumerable<LearnedSignalPattern> rows,string outcome)=>rows.Count(x=>x.Outcome==outcome);
    private static decimal Sign(ServerSignal s)=>s.Direction.Equals("Long",StringComparison.OrdinalIgnoreCase)?1m:-1m;
    private static int Bucket(decimal value,decimal threshold)=>value>threshold?1:value < -threshold?-1:0;
    private static int Volume(decimal value)=>value>=1.2m?1:value<.8m?-1:0;
    internal static bool SimilarGeometry(ServerSignal a,ServerSignal b)
    {
        static (decimal Risk,decimal RewardRisk)? Geometry(ServerSignal s)
        {
            if(s.EntryPrice<=0 || s.TakeProfit<=0 || s.StopLoss<=0 ||
                !(s.Direction.Equals("Long",StringComparison.OrdinalIgnoreCase)||s.Direction.Equals("Short",StringComparison.OrdinalIgnoreCase)))return null;
            var risk=(s.EntryPrice-s.StopLoss)*Sign(s);var reward=(s.TakeProfit-s.EntryPrice)*Sign(s);
            return risk>0 && reward>0 ? (risk/s.EntryPrice,reward/risk):null;
        }
        var left=Geometry(a);var right=Geometry(b);
        if(left is null || right is null)return false;
        var riskRatio=left.Value.Risk/right.Value.Risk;var rrRatio=left.Value.RewardRisk/right.Value.RewardRisk;
        return riskRatio>=.5m && riskRatio<=2m && rrRatio>=.8m && rrRatio<=1.25m;
    }
}

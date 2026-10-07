namespace Phoenix.Web;

public sealed record ElliottCountSection(string Id, string Pattern, long Start, long End,
    IReadOnlyList<ElliottWavePoint> Waves, IReadOnlyList<ElliottWavePoint> Subwaves);
public sealed record ElliottCoverageGap(long Start, long End, string Reason);
public sealed record ElliottCoverageReport(decimal VerifiedPercent, int CandidateCount, int VerifiedCount,
    int SearchLimitedCount, IReadOnlyList<ElliottCountSection> Sections, IReadOnlyList<ElliottCoverageGap> Gaps)
{
    public IReadOnlyList<string> DataIssues { get; init; } = [];
}

/// <summary>Independent historical counts are not silently relabelled as children of the live count.</summary>
public static class ElliottCoverage
{
    public static string CountId(ElliottScenario s) => $"{s.Pattern}:{s.Waves[0].Time}:{s.Waves[^1].Time}";

    public static ElliottCoverageReport Build(IReadOnlyList<ElliottScenario> candidates,
        IReadOnlyList<long> times, ElliottScenario? active = null)
    {
        if (times.Count < 2) return new(0, candidates.Count, 0, 0, [], []);
        var verified = candidates.Where(s => s.ValidationStatus == "Verified" && s.Waves.Count > 1)
            .DistinctBy(CountId).ToArray();
        // The selected active count owns its span. Historical alternatives may
        // touch its endpoints but cannot overwrite or overlap its interior.
        var available = verified.Where(s => active is null || active.ValidationStatus != "Verified" || active.Waves.Count < 2 ||
                s.Waves[^1].Time <= active.Waves[0].Time || s.Waves[0].Time >= active.Waves[^1].Time)
            .OrderBy(s => s.Waves[^1].Time).ThenBy(s => s.Waves[0].Time).ToArray();
        var weights = new decimal[available.Length + 1];
        var previous = new int[available.Length];
        var picked = new bool[available.Length];
        decimal Weight(ElliottScenario s) => times.Count(t => t >= s.Waves[0].Time && t < s.Waves[^1].Time)
            * (1m + s.Score / 10000m);
        for (var i = 0; i < available.Length; i++)
        {
            var j = i - 1;
            while (j >= 0 && available[j].Waves[^1].Time > available[i].Waves[0].Time) j--;
            previous[i] = j;
            var take = weights[j + 1] + Weight(available[i]);
            picked[i] = take > weights[i];
            weights[i + 1] = Math.Max(weights[i], take);
        }
        var selected = new List<ElliottScenario>();
        for (var i = available.Length - 1; i >= 0;)
            if (picked[i]) { selected.Add(available[i]); i = previous[i]; } else i--;
        if (active?.ValidationStatus == "Verified" && active.Waves.Count > 1) selected.Add(active);
        var sections = selected.OrderBy(s => s.Waves[0].Time).Select(s => new ElliottCountSection(
            CountId(s), s.Pattern, s.Waves[0].Time, s.Waves[^1].Time,
            s.Waves.Select(w => w with { Parent = CountId(s), Origin = "History" }).ToArray(),
            s.Subwaves.Select(w => w with { Origin = "Subdivision", Parent = $"{CountId(s)}/{w.Parent}" }).ToArray())).ToArray();
        var gaps = new List<ElliottCoverageGap>();
        var start = times[0];
        foreach (var section in sections)
        {
            if (section.Start > start) AddGap(start, section.Start);
            start = Math.Max(start, section.End);
        }
        if (start < times[^1]) AddGap(start, times[^1]);
        var covered = times.Take(times.Count - 1).Count(t => sections.Any(s => t >= s.Start && t < s.End));
        return new(Math.Round(100m * covered / (times.Count - 1), 1), candidates.Count,
            verified.Length, candidates.Count(s => s.SearchLimitReached), sections, gaps);

        void AddGap(long a, long b)
        {
            var nearby = candidates.Where(s => s.Waves.Count > 1 && s.Waves[0].Time < b && s.Waves[^1].Time > a).ToArray();
            var reason = nearby.Any(s => s.SearchLimitReached) ? "SearchLimit"
                : nearby.Any(s => s.Rules.Any(r => r.Code == "parent-position" && r.Status == "Unknown")) ? "ParentUnknown"
                : nearby.Any(s => s.ValidationStatus == "Unverified") ? "SubdivisionUnknown" : "NoValidStructure";
            gaps.Add(new(a, b, reason));
        }
    }

    public static ElliottScenario Attach(ElliottScenario active, ElliottCoverageReport report)
    {
        var id = CountId(active);
        var history = report.Sections.Where(s => s.Id != id).ToArray();
        var continuations = active.ContextWaves.Where(w => w.Origin == "Continuation").GroupBy(w => w.Parent)
            .Where(g => report.Sections.All(s => g.Max(w => w.Time) <= s.Start || g.Min(w => w.Time) >= s.End))
            .SelectMany(g => g).ToArray();
        var continuationIds = continuations.Select(w => w.Parent).OfType<string>().ToArray();
        return active with
        {
            Waves = active.Waves.Select(w => w with { Parent = id, Origin = "Active" }).ToArray(),
            // These are independent historical roots; only the validator's
            // actual tree establishes parent/subwave relationships.
            ContextWaves = history.SelectMany(s => s.Waves).Concat(continuations).ToArray(),
            Subwaves = active.Subwaves.Where(w => w.Origin != "Continuation")
                .Select(w => w with { Parent = $"{id}/{w.Parent}", Origin = "Subdivision" })
                .Concat(active.Subwaves.Where(w => w.Origin == "Continuation" &&
                    continuationIds.Any(root => w.Parent?.StartsWith(root + "/", StringComparison.Ordinal) == true)))
                .Concat(history.SelectMany(s => s.Subwaves)).ToArray(),
            Coverage = report
        };
    }

    public static ElliottScenario Unavailable(ElliottCoverageReport? report) => new("Unknown", 0, [], [], 0, 0,
        new(0, 0, 0), "NoCount", "Unverified", "", "ساختار قابل تأیید پیدا نشد.")
        { ValidationStatus = "Unavailable", Coverage = report };
}

using Phoenix.Web;

internal static class ElliottCoverageTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("Elliott coverage retains an earlier verified count across an unknown gap", () =>
        {
            var first = Verified("Impulse", [100,120,110,150,130,160]);
            var active = Shift(Verified("Zigzag", [160,140,150,125]), first.Waves[^1].Time + 20);
            var times = Enumerable.Range(0, (int)active.Waves[^1].Time + 1).Select(i => (long)i).ToArray();
            var report = ElliottCoverage.Build([first, active], times, active);
            var attached = ElliottCoverage.Attach(active, report);
            Check(report.Sections.Count == 2 && report.Gaps.Count == 1 && report.VerifiedPercent is > 0 and < 100);
            Check(attached.ContextWaves.Any(w => w.Label == "1" && w.Price == 120));
            Check(attached.Waves.All(w => w.Parent == ElliottCoverage.CountId(active)));
            Check(attached.ContextWaves.All(w => w.Parent == ElliottCoverage.CountId(first)));
            Check(attached.Subwaves.Any(w => w.Parent!.StartsWith(ElliottCoverage.CountId(first) + "/")));
            Check(attached.Subwaves.Any(w => w.Parent!.StartsWith(ElliottCoverage.CountId(active) + "/")));
        });
        run("Elliott coverage never promotes a geometric ABC or a search-limited candidate", () =>
        {
            var (unknown, raw) = ElliottRulesTests.Fixture("Impulse", [100,120,110,150,130,160], firstAsCorrection:true);
            unknown = new ElliottStructureValidator().Validate(unknown, raw) with { SearchLimitReached = true };
            var report = ElliottCoverage.Build([unknown], raw.Select(p => p.Time).ToArray(), unknown);
            Check(report.VerifiedPercent == 0 && report.Sections.Count == 0 && report.Gaps.Single().Reason == "SearchLimit");
            Check(ElliottCoverage.Attach(unknown, report).ValidationStatus == "Unverified");
        });
        run("Elliott coverage chooses compatible broad roots without double counting", () =>
        {
            var first = Verified("Impulse", [100,120,110,150,130,160]);
            var second = Shift(first, first.Waves[^1].Time);
            var overlap = Shift(first, first.Waves[^1].Time / 2);
            var times = Enumerable.Range(0, (int)second.Waves[^1].Time + 1).Select(i => (long)i).ToArray();
            var report = ElliottCoverage.Build([first, second, overlap], times);
            Check(report.Sections.Count == 2 && report.VerifiedPercent == 100 && report.Gaps.Count == 0);
            Check(report.Sections[0].End <= report.Sections[1].Start);
        });
        run("Elliott coverage no-count output explains short data and has no invented waves", () =>
        {
            var candles = ElliottRulesTests.Candles([100,101]);
            var analysis = new ElliottWaveAnalyzer().Analyze(candles);
            var empty = ElliottCoverage.Unavailable(analysis.Coverage);
            Check(analysis.Scenarios.Count == 0 && analysis.Coverage!.Gaps.Single().Reason == "InsufficientCandles");
            Check(empty.Waves.Count == 0 && SignalChartRenderer.DisplayWaves(empty).Count == 0);
            Check(empty.ValidationStatus == "Unavailable");
        });
    }

    private static ElliottScenario Verified(string pattern, decimal[] values)
    {
        var (scenario, raw) = ElliottRulesTests.Fixture(pattern, values);
        var result = new ElliottStructureValidator().Validate(scenario, raw);
        Check(result.ValidationStatus == "Verified");
        return result;
    }
    private static ElliottScenario Shift(ElliottScenario s, long offset) => s with
    {
        Waves = s.Waves.Select(w => w with { Time = w.Time + offset }).ToArray(),
        Subwaves = s.Subwaves.Select(w => w with { Time = w.Time + offset }).ToArray()
    };
    private static void Check(bool value) { if (!value) throw new Exception("Elliott coverage regression failed."); }
}

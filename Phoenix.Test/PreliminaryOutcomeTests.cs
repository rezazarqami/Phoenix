using Phoenix.Web;

internal static class PreliminaryOutcomeTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TechnicalFeatureSnapshot Features = new(50m, 1m, 0m, 0m, 0m,
        1m, 1m, .5m, .5m, 1m, .5m, SimilarityContextVersion: 3);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static LearnedSignalPattern[] Rows(int targets = 8) => Enumerable.Range(0, 10).Select(i =>
        new LearnedSignalPattern(new ServerSignal { Id = Guid.NewGuid(), Symbol = "TESTUSDT",
            Direction = "Long", Timeframe = "15", EntryPrice = 100, TakeProfit = 110, StopLoss = 90,
            CreatedAtUtc = Start.AddDays(i), EntryTriggeredAtUtc = Start.AddDays(i).AddHours(1),
            CompletedAtUtc = Start.AddDays(i).AddHours(2) }, i < targets ? "Target" : "StopLoss", Features)
        { EvidenceStage = "proposal", ObservedAtUtc = Start.AddDays(i) }).ToArray();
    private static ServerSignal Candidate() => new() { Direction = "Long", Timeframe = "15",
        CreatedAtUtc = Start.AddDays(20), TechnicalFeatures = Features };

    public static void FrequenciesNotResemblance()
    {
        var rows = Rows();
        var result = PreliminaryOutcomeEstimator.Estimate(Candidate(), rows, "proposal");
        Check(result.Status == "preliminary" && result.TargetPercent == 78m,
            "Eight targets/two stops with identical technical evidence must use outcome frequency, smoothed toward prior (78%), not 50/50 class similarity");
        Check(PreliminaryOutcomeEstimator.Estimate(Candidate(), Rows(2), "proposal").TargetPercent == 22m,
            "Swapping class counts must swap the estimate within rounding");
        Check(PreliminaryOutcomeEstimator.Estimate(Candidate(), rows.Concat(rows).ToArray(), "proposal") == result,
            "Duplicated records cannot inflate estimates or sample counts");
        var c = Candidate(); c.TechnicalFeatures = Features with { Rsi14 = 0, VolumeRatio = 99, BitcoinMarketAlignment = -1 };
        Check(PreliminaryOutcomeEstimator.Estimate(c, rows, "proposal") == result,
            "Unapproved indicators must not alter the estimate");
        var integrated = SignalSimilarityService.CalculateFromSnapshot(Candidate(),
            new SignalLearningSnapshot([], Start.AddDays(20)) { ProposalPatterns = rows });
        Check(integrated.TargetOutcomePercent == 78m && integrated.StopOutcomePercent == 22m &&
            integrated.OutcomeValidationCount == 0 && integrated.OutcomeEstimate?.Status == "preliminary",
            "Production routing must use the preliminary proposal estimator when no validated model exists");
        var wrongStage = SignalSimilarityService.CalculateFromSnapshot(Candidate(),
            new SignalLearningSnapshot(rows.Select(r => r with { EvidenceStage = "entry" }).ToArray(), Start.AddDays(20)));
        Check(wrongStage.TargetOutcomePercent is null,
            "Descriptive entry fallback must never leak into proposal probabilities");
        var evidence = SignalSimilarityService.CalculateFromPatterns(Candidate(), rows).Evidence;
        var prediction = new SignalSimilarityResult(null, null, 10, 8, 2,
            TargetOutcomePercent: result.TargetPercent, StopOutcomePercent: 100m - result.TargetPercent)
            { Evidence = evidence, OutcomeEstimate = result };
        var text = SignalReviewText.Similarity(prediction);
        Check(text.Trim().Split('\n').Length == 4 && text.Contains("تخمینی، مشروط"),
            "Two probability lines and two unchanged support lines; provisional estimates must be labelled inline");
        Check(!SignalBatchService.PassesOutcomeFilter(prediction, 60m),
            "Provisional values must not silently activate a validated-probability filter");
        Check(SignalBatchService.PassesOutcomeFilter(prediction, 0m), "Disabled filter preserves manual review");
    }

    public static void MissingnessAndTemporalIsolation()
    {
        var rows = Rows(); var c = Candidate();
        Check(PreliminaryOutcomeEstimator.Estimate(c, rows.Take(9).ToArray(), "proposal").TargetPercent is null,
            "Insufficient outcomes must remain unavailable");
        Check(PreliminaryOutcomeEstimator.Estimate(c, Rows(9), "proposal").TargetPercent is null,
            "One-sided samples must not produce overconfident numbers");
        Check(PreliminaryOutcomeEstimator.Estimate(c, rows.Select(r => r with { EvidenceStage = "entry" }).ToArray(), "proposal").TargetPercent is null,
            "Entry history must never masquerade as proposal evidence");
        c.Timeframe = "D";
        Check(PreliminaryOutcomeEstimator.Estimate(c, rows, "proposal").TargetPercent is null,
            "Other timeframes remain descriptive counts, not the probability cohort");
        c = Candidate(); c.CreatedAtUtc = Start.AddDays(5);
        Check(PreliminaryOutcomeEstimator.Estimate(c, rows, "proposal").TargetPercent is null,
            "Results after observation cannot leak into a historical estimate");
        var conflict = rows[0] with { Outcome = "RiskFree" };
        Check(PreliminaryOutcomeEstimator.Estimate(Candidate(), rows.Append(conflict).ToArray(), "proposal").TargetPercent is null,
            "Conflicts with nonbinary outcomes must be excluded before binary filtering");
        var distant = Candidate(); distant.TechnicalFeatures = Features with {
            IchimokuPosition = -1, IchimokuCloudBias = -1, SupportDistanceAtr = 100, ResistanceDistanceAtr = 100 };
        Check(PreliminaryOutcomeEstimator.Estimate(distant, rows, "proposal").TargetPercent is null,
            "No nearby evidence must remain unknown rather than show an unrelated population prior");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { PreliminaryOutcomeEstimator.Estimate(Candidate(), rows, "proposal", cts.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
    }

    public static void TrainingReportsAndProposalClock()
    {
        OutcomeTrainingReport? report = null;
        Check(SignalOutcomeModel.Train(Rows(), report: x => report = x) is null &&
            report is { Status: "insufficient-completed-evidence", EligibleCount: 10 },
            "Failed validation must expose a reason and usable sample count without a user-facing panel");
        var history = Enumerable.Range(0, 100).Select(i => new LearnedSignalPattern(new ServerSignal {
            Id = Guid.NewGuid(), Direction = "Long", CreatedAtUtc = Start.AddDays(i),
            EntryTriggeredAtUtc = Start.AddDays(i).AddHours(1), CompletedAtUtc = Start.AddDays(i).AddHours(2) },
            i % 2 == 0 ? "Target" : "StopLoss", Features with { StopLevelStrength = i % 2 == 0 ? .9m : .1m })
            { EvidenceStage = "proposal", ObservedAtUtc = Start.AddDays(i) }).ToArray();
        var model = SignalOutcomeModel.Train(history, report: x => report = x);
        Check(model is not null && report?.Status == "validated", "Strong repeatable evidence must still validate");
        var c = Candidate(); c.EntryTriggeredAtUtc = Start.AddDays(150);
        Check(model!.Predict(c, history[0].Features, "proposal") is null,
            "A late entry cannot make a model trained after proposal available retroactively");
    }
}

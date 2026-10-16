using Phoenix.Web;

internal static class OutcomeModelTests
{
    private static readonly TechnicalFeatureSnapshot Basic = new(0.5m, 1m, 0m, 0m, 0m,
        1m, 1m, 0.5m, 0.5m, 1m, 0.5m);
    private static LearnedSignalPattern[] History(bool invertHoldout = false) =>
        Enumerable.Range(0, 100).Select(i =>
        {
            var entry = new DateTime(2026, 1, 1).AddDays(i);
            var target = i % 2 == 0;
            var f = Basic with { StopLevelStrength = target ? 0.9m : 0.1m };
            return new LearnedSignalPattern(new ServerSignal { Id = Guid.NewGuid(), Direction = "Long",
                EntryTriggeredAtUtc = entry, CompletedAtUtc = entry.AddHours(1) },
                (invertHoldout && i >= 80 ? !target : target) ? "Target" : "StopLoss", f);
        }).ToArray();
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    public static void PlannedLevelGeometry()
    {
        var signal = new ServerSignal { Direction = "Long" };
        var a = Basic with { TradeGeometry = new(1m, 1m, 2m, 2m, 5m) };
        var b = Basic with { TradeGeometry = new(1m, 3m, 1m, 1m/3m, 5m) };
        Check(!SignalOutcomeModel.Values(signal, a).SequenceEqual(SignalOutcomeModel.Values(signal, b)),
            "Identical indicators with different target/stop placement must have different model inputs");
        Check(SignalOutcomeModel.Values(signal, Basic).TakeLast(5).All(x => x is null),
            "Missing historical geometry must remain missing, not fabricated");
    }

    public static void LearnsOutcomesAndValidates()
    {
        var history = History();
        var model = SignalOutcomeModel.Train(history);
        Check(model is not null, "Repeatable held-out distinction should validate");
        Check(model!.ValidationCount == 20 && model.ValidationBrier < model.BaselineBrier,
            "Only newer untouched outcomes validate the fitted model");
        Check(model.CalibrationCount == 20 && model.ValidationLogLoss < model.BaselineLogLoss,
            "A separate calibration block and proper scoring rules are required");
        Check(model.Reliability.Sum(x => x.Count) == 20,
            "Reliability bins must account for all held-out predictions");
        var signal = new ServerSignal { Direction = "Long" };
        var target = model.Predict(signal, history[0].Features);
        var stop = model.Predict(signal, history[1].Features);
        Check(target > 65m && stop < 35m, "Outcome labels must teach an actual decision boundary");
        Check(model.Predict(signal, history[0].Features with { Rsi14 = 0m, VolumeRatio = 9m,
            BitcoinMarketAlignment = -1m, HigherTimeframeAlignment = -1m, PriceActionScore = 0m }) == target,
            "Unapproved indicators cannot change the estimate");
        Check(model.Predict(signal, Basic) is null, "Missing learned evidence must not masquerade as confidence");
        Check(model.Predict(history[0].Signal, history[0].Features) is null,
            "A current model must not predict an earlier trade using future training outcomes");
        Check(model.Predict(new ServerSignal { Direction = "Long", CreatedAtUtc = new DateTime(2026, 1, 1) },
            history[0].Features) is null, "Historical proposals also require past-only evidence");
    }

    public static void RejectsUnvalidatedAndLeakedEvidence()
    {
        Check(SignalOutcomeModel.Train(History(true)) is null, "Failed later validation must withhold estimates");
        var constant = History().Select(x => x with { Features = Basic }).ToArray();
        Check(SignalOutcomeModel.Train(constant) is null, "Indistinguishable classes cannot produce a decisive score");
        var future = History();
        foreach (var row in future) row.Signal.CompletedAtUtc = new DateTime(2027, 1, 1);
        Check(SignalOutcomeModel.Train(future) is null, "Outcomes unknown at validation start cannot train");
        var duplicate = History().Take(10).ToArray();
        Check(SignalOutcomeModel.Train(Enumerable.Range(0, 10).SelectMany(_ => duplicate).ToArray()) is null,
            "Duplicating records cannot satisfy sample requirements");
        var similarityOnly = new SignalSimilarityResult(90m, 89m, 100, 50, 50);
        Check(!SignalBatchService.PassesOutcomeFilter(similarityOnly, 55m),
            "High resemblance cannot satisfy an outcome probability filter");
        Check(SignalBatchService.PassesOutcomeFilter(similarityOnly, 0m),
            "A disabled filter still allows human review when the model abstains");
        Check(SignalBatchService.PassesOutcomeFilter(similarityOnly with { TargetOutcomePercent = 60m, OutcomeValidationCount = 20 }, 55m),
            "Validated estimates must drive the configured threshold");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { SignalOutcomeModel.Train(History(), cts.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
    }

    public static void CalibrationAndAuditIsolation()
    {
        var scores = Enumerable.Range(0, 200).Select(i => i < 100 ? 6d : -6d).ToArray();
        var outcomes = Enumerable.Range(0, 200).Select(i => i < 100 ? i % 10 < 7 : i % 10 < 3).ToArray();
        var (slope, intercept) = OutcomeCalibration.Fit(scores, outcomes);
        var p = 1d / (1d + Math.Exp(-(slope * 6d + intercept)));
        Check(p > 0.65d && p < 0.8d,
            "Overconfident scores should be calibrated toward observed 70 percent outcomes");
        var reversed = OutcomeCalibration.Fit(scores, outcomes.Select(x => !x).ToArray());
        Check(reversed.Slope >= 0d, "Calibration cannot reverse ranking to rescue a failed classifier");

        var original = History();
        var changed = original.Select((row, i) => i is 82 or 83
            ? row with { Outcome = row.Outcome == "Target" ? "StopLoss" : "Target" } : row).ToArray();
        var a = SignalOutcomeModel.Train(original);
        var b = SignalOutcomeModel.Train(changed);
        Check(a is not null && b is not null, "Both audits retain a strong signal");
        var candidate = new ServerSignal { Direction = "Long" };
        Check(a!.Predict(candidate, original[0].Features) == b!.Predict(candidate, original[0].Features),
            "Changing audit labels may affect acceptance, never the fitted probabilities");
        Check(a.ValidationBrier != b.ValidationBrier, "Audit scores must still reflect changed labels");

        var delayed = History();
        foreach (var row in delayed.Skip(60).Take(20))
            row.Signal.CompletedAtUtc = new DateTime(2027, 1, 1);
        Check(SignalOutcomeModel.Train(delayed) is null,
            "Calibration outcomes unavailable at audit time cannot leak into calibration");
        var recentDrift = History().Select((row, i) => i >= 90
            ? row with { Outcome = row.Outcome == "Target" ? "StopLoss" : "Target" } : row).ToArray();
        Check(SignalOutcomeModel.Train(recentDrift) is null, "Recent reversed outcomes must block publication");
    }

    public static void SeparatesEvidenceStages()
    {
        var entry = History();
        var proposal = entry.Select(p => p with { EvidenceStage = "proposal", ObservedAtUtc = p.ObservedAtUtc!.Value.AddHours(-1) }).ToArray();
        var entryModel = SignalOutcomeModel.Train(entry)!;
        var proposalModel = SignalOutcomeModel.Train(proposal)!;
        var candidate = new ServerSignal { Direction="Long" };
        Check(entryModel.Predict(candidate,entry[0].Features,"proposal") is null,
            "An entry model cannot predict proposal-time evidence.");
        Check(proposalModel.Predict(candidate,proposal[0].Features,"entry") is null,
            "A proposal model cannot predict entry-time evidence.");
        Check(proposalModel.Predict(candidate,proposal[0].Features,"proposal") is not null,
            "A separately validated proposal model should retain its own prediction.");
        Check(SignalOutcomeModel.Train(entry.Concat(proposal).ToArray()) is null,
            "Evidence from different observation stages must never be pooled.");
    }
}

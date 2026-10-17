namespace Phoenix.Web;

/// <summary>A fully isolated, non-trading queue for approved observation signals.</summary>
public sealed class ShadowSignalRuntime
{
    public ServerOrderStore Store { get; }

    public ShadowSignalRuntime()
    {
        var mainPath = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json");
        var directory = Path.GetDirectoryName(mainPath)!;
        Store = new ServerOrderStore(Path.Combine(directory, "shadow-signals.json"),
            Path.Combine(directory, "shadow-history.db"));
    }

    public async Task<ServerSignal> AddAsync(SignalCandidate candidate, string timeframe, bool lineMode,
        byte[] image, ProfessionalSignalAnalysis analysis,
        string? requestedByUsername, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var signal = new ServerSignal
        {
            Id = id, Symbol = candidate.Symbol, Direction = candidate.Direction,
            Ceiling = candidate.Ceiling, Floor = candidate.Floor,
            EntryPrice = candidate.EntryPrice, TakeProfit = candidate.TakeProfit,
            StopLoss = candidate.StopLoss, LastPrice = candidate.LastPrice,
            StopLoss2 = candidate.StopLoss2 ?? candidate.EntryPrice + (candidate.TakeProfit - candidate.EntryPrice) * .50m,
            RiskFreePrice = candidate.RiskFreePrice ?? candidate.EntryPrice + (candidate.TakeProfit - candidate.EntryPrice) * .75m,
            // Observation policy v1 stays unchanged; live protection is managed separately.
            RiskFreeStopMarket = candidate.EntryPrice + (candidate.TakeProfit - candidate.EntryPrice) * .25m,
            Leverage = candidate.Leverage,
            ObservationPolicyVersion = 1,
            ExpirePrice = candidate.Direction == "Long" ? candidate.Ceiling : candidate.Floor,
            Status = "Pending", OrderLinkId = $"observe-{id:N}"[..36], CreatedAtUtc = DateTime.UtcNow,
            RequestedByUsername = requestedByUsername, Timeframe = timeframe,
            ChartMode = lineMode ? "Line" : "Candles", TechnicalFeatures = analysis.Features,
            ProposalEvidence = SignalProposalEvidence.Capture(candidate, timeframe, analysis),
            AnalysisSummary = string.Join(" | ", analysis.Strengths.Concat(analysis.Risks)),
            MarketRegime = analysis.MarketRegime,
            TargetSimilarityPercent = analysis.Prediction.TargetPercent,
            StopSimilarityPercent = analysis.Prediction.StopPercent,
            TargetOutcomePercent = analysis.Prediction.TargetOutcomePercent,
            StopOutcomePercent = analysis.Prediction.StopOutcomePercent,
            OutcomeValidationCount = analysis.Prediction.OutcomeValidationCount,
            SimilaritySampleCount = analysis.Prediction.SampleCount,
            SimilarityEvidence = analysis.Prediction.Evidence
        };
        await Store.AddAsync(signal, token,
            new SignalEvidence(timeframe, signal.ChartMode, image, analysis.Features,
                signal.AnalysisSummary, signal.MarketRegime));
        return signal;
    }
}

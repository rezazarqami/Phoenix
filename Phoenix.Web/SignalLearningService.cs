namespace Phoenix.Web;

public sealed record LearnedSignalPattern(ServerSignal Signal, string Outcome,
    TechnicalFeatureSnapshot Features)
{
    public DateTime? ObservedAtUtc { get; init; } = Signal.EntryTriggeredAtUtc;
    public string EvidenceStage { get; init; } = "entry";
}

public sealed record SignalLearningSnapshot(IReadOnlyList<LearnedSignalPattern> Patterns,
    DateTime RefreshedAtUtc)
{
    internal SignalSimilarityMetric? SimilarityMetric { get; init; }
    internal SignalOutcomeModel? OutcomeModel { get; init; }
    internal SignalOutcomeModel? ProposalOutcomeModel { get; init; }
    public IReadOnlyList<LearnedSignalPattern> ProposalPatterns { get; init; } = [];
    internal IReadOnlyList<(string Source,SignalHistoryItem Item)> OutcomeHistory { get; init; } = [];
    public static readonly SignalLearningSnapshot Empty = new([], DateTime.MinValue);
}

/// <summary>Prepares completed-signal technical fingerprints before proposals are requested.</summary>
public sealed class SignalLearningService(
    ServerOrderStore store,
    ShadowSignalRuntime shadow,
    ILogger<SignalLearningService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile SignalLearningSnapshot _snapshot = SignalLearningSnapshot.Empty;

    public Task RefreshNowAsync(CancellationToken token) => RefreshAsync(token);

    public async Task<SignalLearningSnapshot> GetSnapshotAsync(CancellationToken token = default)
    {
        if (_snapshot.RefreshedAtUtc == DateTime.MinValue) await RefreshAsync(token);
        return _snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Phoenix technical learning refresh failed."); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        await _refreshGate.WaitAsync(token);
        try
        {
            var outcomeHistory = (await store.GetLearningHistoryAsync(token)).Select(x=>(Source:"main",Item:x))
                .Concat((await shadow.Store.GetLearningHistoryAsync(token)).Select(x=>(Source:"observation",Item:x))).ToArray();
            var (patterns, proposals) = BuildPatterns(outcomeHistory.Select(x=>x.Item));
            _snapshot = new(patterns, DateTime.UtcNow)
            {
                OutcomeHistory = outcomeHistory,
                ProposalPatterns = proposals,
                SimilarityMetric = null };
            logger.LogInformation("Phoenix loaded {EntryCount} entry and {ProposalCount} proposal fingerprints; outcome probability disabled.",
                patterns.Count, proposals.Length);
        }
        finally { _refreshGate.Release(); }
    }
    // Validity depends on the measurements actually shared, not the schema age.
    internal static (List<LearnedSignalPattern> Entries, LearnedSignalPattern[] Proposals)
        BuildPatterns(IEnumerable<SignalHistoryItem> items)
    {
        var history=items;
        var completed = history.Select(x => x.Signal)
            .Where(x => x.Outcome is "Target" or "StopLoss" or "RiskFree")
            .Where(x => x.EntryPrice > 0m && x.Ceiling > x.Floor)
            .GroupBy(x => x.Id).Select(x => x.First()).ToArray();
        var patterns = new List<LearnedSignalPattern>(completed.Length);
        foreach (var signal in completed)
        {
            // Proposal-time snapshots and archived review candles describe
            // a different moment. Never relabel them as entry-time evidence.
            if (signal.EntryTriggeredAtUtc is not null && signal.CompletedAtUtc > signal.EntryTriggeredAtUtc &&
                signal.EntryTechnicalFeatures is { } features)
                patterns.Add(new(signal, signal.Outcome!, features));
        }
        var proposals = completed.Where(x => x.EntryTriggeredAtUtc is not null &&
                x.CompletedAtUtc > x.EntryTriggeredAtUtc && x.ProposalEvidence is { } p &&
                p.ObservedAtUtc <= x.EntryTriggeredAtUtc)
            .Select(x => new LearnedSignalPattern(new ServerSignal
                {
                    Id=x.Id, Symbol=x.ProposalEvidence!.Symbol, Direction=x.ProposalEvidence.Direction,
                    Timeframe=x.ProposalEvidence.Timeframe, EntryPrice=x.ProposalEvidence.EntryPrice,
                    TakeProfit=x.ProposalEvidence.TakeProfit, StopLoss=x.ProposalEvidence.StopLoss,
                    EntryTriggeredAtUtc=x.EntryTriggeredAtUtc, CompletedAtUtc=x.CompletedAtUtc,
                    CreatedAtUtc=x.ProposalEvidence.ObservedAtUtc
                }, x.Outcome!, x.ProposalEvidence!.Features)
                { ObservedAtUtc = x.ProposalEvidence!.ObservedAtUtc, EvidenceStage = "proposal" }).ToArray();
        return (patterns, proposals);
    }

}

namespace Phoenix.Web;

public sealed record LearnedSignalPattern(ServerSignal Signal, string Outcome,
    TechnicalFeatureSnapshot Features);

public sealed record SignalLearningSnapshot(IReadOnlyList<LearnedSignalPattern> Patterns,
    DateTime RefreshedAtUtc)
{
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
            var history = (await store.GetHistoryAsync(3650, 5000, token))
                .Concat(await shadow.Store.GetHistoryAsync(3650, 5000, token));
            var completed = history.Select(x => x.Signal)
                .Where(x => x.Outcome is "Target" or "StopLoss")
                .Where(x => x.EntryPrice > 0m && x.Ceiling > x.Floor)
                .ToArray();
            var patterns = new List<LearnedSignalPattern>(completed.Length);
            foreach (var signal in completed)
            {
                // Proposal-time snapshots and archived review candles describe
                // a different moment. Never relabel them as entry-time evidence.
                if (signal.EntryTriggeredAtUtc is not null && signal.EntryTechnicalFeatures is { } features)
                    patterns.Add(new(signal, signal.Outcome!, features));
            }
            _snapshot = new(patterns, DateTime.UtcNow);
            logger.LogInformation("Phoenix learned {Count} completed technical patterns.", patterns.Count);
        }
        finally { _refreshGate.Release(); }
    }
}

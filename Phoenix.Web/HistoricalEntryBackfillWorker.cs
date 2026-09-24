using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

/// <summary>Reconstructs completed trades at their historical entry, once per persisted sample.</summary>
public sealed class HistoricalEntryBackfillWorker(
    ServerOrderStore store, ShadowSignalRuntime shadow, BybitDemoClient bybit,
    SignalLearningService learning, ILogger<HistoricalEntryBackfillWorker> logger) : BackgroundService
{
    private readonly Dictionary<Guid, DateTime> _retryAfter = [];
    private static readonly string[] ContextIntervals = ["15", "60", "240", "D", "W", "M"];

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(12), token);
        while (!token.IsCancellationRequested)
        {
            var found = 0;
            var processed = 0;
            try
            {
                var real = (await store.GetHistoryAsync(3650, 5000, token))
                    .Select(x => (Signal: x.Signal, Destination: store));
                var observed = (await shadow.Store.GetHistoryAsync(3650, 5000, token))
                    .Select(x => (Signal: x.Signal, Destination: shadow.Store));
                var missing = real.Concat(observed)
                    .Where(x => x.Signal.Outcome is "Target" or "StopLoss" &&
                        x.Signal.EntryTechnicalFeatures is null &&
                        x.Signal.EntryPrice > 0m && x.Signal.Ceiling > x.Signal.Floor &&
                        x.Signal.Timeframe is not null && EntryTime(x.Signal) is not null)
                    .Where(x => !_retryAfter.TryGetValue(x.Signal.Id, out var until) || until <= DateTime.UtcNow)
                    .OrderByDescending(x => EntryTime(x.Signal)).Take(4).ToArray();
                processed = missing.Length;
                foreach (var (signal, destination) in missing)
                {
                    token.ThrowIfCancellationRequested();
                    _retryAfter[signal.Id] = DateTime.UtcNow.AddHours(1);
                    try
                    {
                        var entryAt = EntryTime(signal)!.Value;
                        var features = await ReconstructAsync(signal, entryAt, token);
                        if (await destination.SaveHistoricalEntryFeaturesAsync(signal.Id, entryAt, features, token))
                        {
                            found++;
                            logger.LogInformation("Reconstructed entry snapshot for {SignalId} at {EntryAt}",
                                signal.Id, entryAt);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Historical entry data unavailable for {SignalId}", signal.Id);
                    }
                }
                if (found > 0) await learning.RefreshNowAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Historical entry reconstruction failed"); }
            try { await Task.Delay(processed > 0 ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(1), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static DateTime? EntryTime(ServerSignal signal) =>
        signal.EntryTriggeredAtUtc ?? signal.FilledAtUtc ?? signal.SubmittedAtUtc;

    private async Task<TechnicalFeatureSnapshot> ReconstructAsync(ServerSignal signal,
        DateTime entryAt, CancellationToken token)
    {
        var interval = signal.Timeframe!;
        var primary = ProfessionalSignalAnalysisService.CandlesAvailableAt(
            await bybit.GetKlinesBeforeAsync(signal.Symbol, interval, entryAt, 1000, token),
            interval, entryAt);
        if (primary.Count < 52) throw new InvalidOperationException("Fewer than 52 closed entry candles.");
        var candidate = new SignalCandidate(signal.Symbol, interval, signal.Direction,
            signal.Ceiling, signal.Floor, signal.EntryPrice, signal.EntryPrice,
            signal.TakeProfit, signal.StopLoss, signal.StopLoss2, signal.RiskFreePrice,
            signal.Leverage ?? 1m, signal.Quantity, 0m,
            primary[0].OpenTime, primary[^1].OpenTime, primary[0].OpenTime, primary[^1].OpenTime,
            primary.Count, "Historical entry", false, primary[^1].OpenTime);
        var histories = new Dictionary<string, IReadOnlyList<BybitKline>> { [interval] = primary };
        foreach (var context in ContextIntervals.Where(x => x != interval))
        {
            try
            {
                var candles = ProfessionalSignalAnalysisService.CandlesAvailableAt(
                    await bybit.GetKlinesBeforeAsync(signal.Symbol, context, entryAt, 1000, token),
                    context, entryAt);
                if (candles.Count >= 80) histories[context] = candles;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogDebug(ex, "No {Interval} context at historical entry", context); }
        }
        return TechnicalFeatureExtractor.Calculate(primary, candidate) with
        {
            MultiScaleLevels = MultiScaleLevelExtractor.Calculate(histories, candidate)
        };
    }
}

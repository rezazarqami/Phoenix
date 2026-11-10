using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

/// <summary>Follows the market after a protected exit, independently of the closed trade.</summary>
public sealed class RiskFreePathWorker(
    ServerOrderStore store, ShadowSignalRuntime shadow, BybitDemoClient bybit,
    ILogger<RiskFreePathWorker> logger) : BackgroundService
{
    internal const long Minute = 60_000;
    private const int PageSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(token))
        {
            try
            {
                foreach (var destination in new[] { store, shadow.Store })
                {
                    var jobs = await destination.GetRiskFreePathsAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), token);
                    await Parallel.ForEachAsync(jobs, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 4, CancellationToken = token
                    }, (job, ct) => ProcessAsync(destination, job, ct));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not process the protected-exit price paths"); }
        }
    }

    private async ValueTask ProcessAsync(ServerOrderStore destination, RiskFreePathJob job, CancellationToken token)
    {
        var signal = job.Signal;
        var closedAt = new DateTimeOffset((signal.RiskFreeClosedAtUtc ?? signal.CompletedAtUtc)!.Value
            .ToUniversalTime()).ToUnixTimeMilliseconds();
        var start = job.NextCandle ?? FloorMinute(closedAt);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Give the exchange one full minute to publish the final candle.
        var end = Math.Min(FloorMinute(now) - Minute, start + PageSize * Minute);
        var progress = new RiskFreePathProgress(start);
        var retryAfter = Minute;
        if (!ValidLevels(signal)) progress = new(start, "Indeterminate");
        else if (end > start)
        {
            try
            {
                var candles = await bybit.GetKlinesBeforeAsync(signal.Symbol, "1",
                    DateTimeOffset.FromUnixTimeMilliseconds(end - 1).UtcDateTime, PageSize, token);
                progress = Follow(signal, closedAt, start, end, candles);
                retryAfter = progress.NextCandle < end ? 15 * Minute : 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Price path unavailable for protected exit {SignalId}", signal.Id);
                retryAfter = 15 * Minute;
            }
        }
        await destination.SaveRiskFreePathAsync(job, progress, now + retryAfter, token);
    }

    internal static RiskFreePathProgress Follow(ServerSignal signal, long closedAt,
        long start, long end, IReadOnlyList<BybitKline> candles)
    {
        var next = start;
        foreach (var candle in candles)
        {
            if (candle.OpenTime < next) continue;
            if (candle.OpenTime >= end || candle.OpenTime != next || candle.Low <= 0 ||
                candle.Low > Math.Min(candle.Open, candle.Close) ||
                candle.High < Math.Max(candle.Open, candle.Close)) break;

            var favorable = signal.Direction == "Long" ? candle.High : candle.Low;
            var adverse = signal.Direction == "Long" ? candle.Low : candle.High;
            var target = DemoOrderWorker.TargetReached(signal, favorable);
            var stop = DemoOrderWorker.StopLossReached(signal, adverse);
            if (target || stop)
            {
                // The opening price can establish order when both levels are touched in a full candle.
                if (next >= closedAt)
                {
                    if (DemoOrderWorker.TargetReached(signal, candle.Open)) return new(next, "Target", next);
                    if (DemoOrderWorker.StopLossReached(signal, candle.Open)) return new(next, "StopLoss", next);
                }
                // In the exit minute, a wick may have occurred before the trade closed.
                // The close proves a post-exit touch only when the other level was never touched.
                if (target && !stop && (next >= closedAt || DemoOrderWorker.TargetReached(signal, candle.Close)))
                    return new(next, "Target", next + Minute);
                if (stop && !target && (next >= closedAt || DemoOrderWorker.StopLossReached(signal, candle.Close)))
                    return new(next, "StopLoss", next + Minute);
                return new(next, "Indeterminate");
            }
            next += Minute;
        }
        return new(next);
    }

    private static bool ValidLevels(ServerSignal signal) =>
        signal.EntryPrice > 0 && signal.StopLoss > 0 && signal.TakeProfit > 0 &&
        (signal.Direction == "Long" && signal.StopLoss < signal.EntryPrice && signal.EntryPrice < signal.TakeProfit ||
         signal.Direction == "Short" && signal.TakeProfit < signal.EntryPrice && signal.EntryPrice < signal.StopLoss);

    private static long FloorMinute(long time) => time - time % Minute;
}

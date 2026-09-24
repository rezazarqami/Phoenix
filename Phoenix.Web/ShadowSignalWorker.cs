using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

/// <summary>Observes prices only; it never submits orders and never sends notifications.</summary>
public sealed class ShadowSignalWorker(
    ShadowSignalRuntime runtime,
    BybitDemoClient bybit,
    ProfessionalSignalAnalysisService professional,
    ILogger<ShadowSignalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var signals = await runtime.Store.GetAllAsync(token);
                foreach (var group in signals
                    .Where(x => x.CompletedAtUtc is null && x.Status is "Pending" or "Filled")
                    .GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase))
                {
                    var price = (await bybit.GetLastPriceAsync(group.Key, token)).LastPrice;
                    foreach (var signal in group)
                        await TrackAsync(signal, price, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Shadow signal tracking cycle failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TrackAsync(ServerSignal signal, decimal price, CancellationToken token)
    {
        signal.LastPrice = price;
        if (signal.Status == "Pending")
        {
            if (DemoOrderWorker.EntryReached(signal, price))
            {
                signal.Status = "Filled";
                signal.FilledAtUtc = DateTime.UtcNow;
                signal.EntryTriggeredAtUtc = signal.FilledAtUtc;
                signal.AverageFillPrice = signal.EntryPrice;
                await runtime.Store.UpdateAsync(signal, token);
                try
                {
                    var interval = signal.Timeframe ?? "15";
                    var candles = await bybit.GetKlinesAsync(signal.Symbol, interval, 1000, token);
                    var candidate = new SignalCandidate(signal.Symbol, interval, signal.Direction,
                        signal.Ceiling, signal.Floor, signal.EntryPrice, signal.EntryPrice,
                        signal.TakeProfit, signal.StopLoss, signal.StopLoss2, signal.RiskFreePrice,
                        signal.Leverage ?? 1m, signal.Quantity, 0m,
                        candles[0].OpenTime, candles[^1].OpenTime,
                        candles[0].OpenTime, candles[^1].OpenTime,
                        candles.Count, "Observed entry", false, candles[^1].OpenTime);
                    var analysis = await professional.AnalyzeAsync(candidate, candles, interval, token,
                        signal.EntryTriggeredAtUtc);
                    await runtime.Store.SaveEntryTechnicalFeaturesAsync(signal.Id, analysis.Features,
                        signal.EntryTriggeredAtUtc.Value, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Entry fingerprint unavailable for {Symbol}", signal.Symbol); }
            }
            else if (InitialExpiryReached(signal, price))
                await runtime.Store.RemoveAsync(signal.Id, token);
            else
                await runtime.Store.UpdateAsync(signal, token);
            return;
        }

        if (DemoOrderWorker.TargetReached(signal, price)) Complete(signal, "Target");
        else if (DemoOrderWorker.StopLossReached(signal, price))
        {
            Complete(signal, "StopLoss");
            signal.FailureReason = ProfessionalSignalAnalysisService.ExplainStop(signal.TechnicalFeatures);
        }
        await runtime.Store.UpdateAsync(signal, token);
    }

    private static bool InitialExpiryReached(ServerSignal signal, decimal price) => signal.Direction switch
    {
        "Long" => price >= signal.Ceiling,
        "Short" => price <= signal.Floor,
        _ => false
    };

    private static void Complete(ServerSignal signal, string outcome)
    {
        var now = DateTime.UtcNow;
        signal.Status = "Completed";
        signal.Outcome = outcome;
        signal.CompletedAtUtc = now;
        if (outcome == "Target") signal.TargetReachedAtUtc = now;
        else signal.StopLossReachedAtUtc = now;
    }
}

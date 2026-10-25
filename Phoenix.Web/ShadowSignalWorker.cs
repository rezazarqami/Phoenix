using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

/// <summary>Observes prices only; it never submits orders and never sends notifications.</summary>
public sealed class ShadowSignalWorker(
    ShadowSignalRuntime runtime,
    BybitDemoClient bybit,
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
                    try
                    {
                        var price = (await bybit.GetLastPriceAsync(group.Key, token)).LastPrice;
                        if (price <= 0) continue;
                        foreach (var signal in group)
                        {
                            try { await TrackAsync(signal, price, token); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch (Exception ex) { logger.LogWarning(ex, "Observation update failed for {SignalId}", signal.Id); }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogWarning(ex, "Observation price unavailable for {Symbol}", group.Key); }
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
                // The entry sample can already be beyond the stop after a market gap.
                ObservationOutcomeTracker.TrackFilled(signal, price, signal.EntryTriggeredAtUtc.Value);
                await runtime.Store.UpdateAsync(signal, token);
                // Tracking entry changes lifecycle only. Proposal evidence is frozen;
                // no candle fetch or second technical analysis is needed here.
            }
            else if (InitialExpiryReached(signal, price))
            {
                ObservationOutcomeTracker.Expire(signal, DateTime.UtcNow);
                await runtime.Store.UpdateAsync(signal, token);
            }
            else
                await runtime.Store.UpdateAsync(signal, token);
            return;
        }

        ObservationOutcomeTracker.TrackFilled(signal, price, DateTime.UtcNow);
        await runtime.Store.UpdateAsync(signal, token);
    }

    private static bool InitialExpiryReached(ServerSignal signal, decimal price) => signal.Direction switch
    {
        "Long" => price >= signal.Ceiling,
        "Short" => price <= signal.Floor,
        _ => false
    };

}

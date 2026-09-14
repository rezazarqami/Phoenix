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
                signal.AverageFillPrice = signal.EntryPrice;
                await runtime.Store.UpdateAsync(signal, token);
            }
            else if (InitialExpiryReached(signal, price))
                await runtime.Store.RemoveAsync(signal.Id, token);
            else
                await runtime.Store.UpdateAsync(signal, token);
            return;
        }

        if (DemoOrderWorker.TargetReached(signal, price)) Complete(signal, "Target");
        else if (DemoOrderWorker.StopLossReached(signal, price)) Complete(signal, "StopLoss");
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

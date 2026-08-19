using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class Strategy2Worker(
    Strategy2Runtime runtime,
    Strategy2TelegramNotifier telegram,
    ILogger<Strategy2Worker> logger) : BackgroundService
{
    public Task SubmitClaimedAsync(ServerSignal order, CancellationToken token) => SubmitAsync(order, token);

    public async Task ExpireBecauseBusyAsync(ServerSignal order, CancellationToken token)
    {
        Complete(order, "Expired", "PositionAlreadyOpen");
        await runtime.Store.UpdateAsync(order, token);
        await telegram.ExpiredAsync(order, token);
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (runtime.Options.Enabled)
                    await ProcessCycleAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogWarning(exception, "Strategy 2 worker cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    private async Task ProcessCycleAsync(CancellationToken token)
    {
        var orders = await runtime.Store.GetAllAsync(token);
        foreach (var group in orders.Where(IsActive).GroupBy(x => x.Symbol))
        {
            var price = (await runtime.Client.GetLastPriceAsync(group.Key, token)).LastPrice;
            foreach (var order in group)
            {
                order.LastPrice = price;
                if (order.Status == "Pending") await TrackPendingAsync(order, price, token);
                else if (order.Status == "Submitted") await SynchronizeAsync(order, price, token);
                else if (order.Status == "Filled") await TrackLevelsAsync(order, price, token);
            }
        }
    }

    private async Task TrackPendingAsync(ServerSignal order, decimal price, CancellationToken token)
    {
        if (order.ExpirePrice == 0) order.ExpirePrice = order.Direction == "Long" ? order.Ceiling : order.Floor;
        if (order.ExpireActivationPrice == 0)
            order.ExpireActivationPrice = order.EntryPrice + 0.20m * (order.TakeProfit - order.EntryPrice);

        if (order.ExpireStage == "Initial" && InitialExpiryReached(order, price))
        {
            Complete(order, "Expired", "InitialBoundary");
            await runtime.Store.UpdateAsync(order, token);
            await telegram.ExpiredAsync(order, token);
            return;
        }
        if (order.ExpireStage == "Initial" && DemoOrderWorker.ExpireActivationReached(order, price))
        {
            order.ExpireStage = "Target";
            order.ExpirePrice = order.TakeProfit;
            order.ExpireAdjustedAtUtc = DateTime.UtcNow;
        }
        if (order.ExpireStage == "Target" && DemoOrderWorker.TargetExpiryReached(order, price))
        {
            Complete(order, "Expired", "TargetAfterActivation");
            await runtime.Store.UpdateAsync(order, token);
            await telegram.ExpiredAsync(order, token);
            return;
        }

        if (DemoOrderWorker.EntryReached(order, price))
        {
            var claim = await runtime.Store.TryClaimExclusiveSubmissionAsync(order.Id, price, token);
            if (claim == ExclusiveClaimResult.Claimed)
            {
                var claimed = (await runtime.Store.GetAllAsync(token)).Single(x => x.Id == order.Id);
                await SubmitAsync(claimed, token);
                return;
            }
            if (claim == ExclusiveClaimResult.PositionBusy)
            {
                Complete(order, "Expired", "PositionAlreadyOpen");
                await runtime.Store.UpdateAsync(order, token);
                await telegram.ExpiredAsync(order, token);
                return;
            }
        }
        await runtime.Store.UpdateAsync(order, token);
    }

    private async Task SubmitAsync(ServerSignal order, CancellationToken token)
    {
        try
        {
            var available = await runtime.Client.GetAvailableBalanceAsync(token);
            var rules = await runtime.Client.GetInstrumentRulesAsync(order.Symbol, token);
            // Keep a small exchange reserve for entry/exit fees and initial-margin
            // fluctuations; otherwise a nominal 99.5% allocation can exceed AB.
            order.PositionSizeUsdt = available * runtime.Options.BalanceUsageRatio * 0.97m;
            order.ApplyPhoenixLeverage(rules);
            if (order.PositionSizeUsdt <= 0 || order.Quantity < rules.MinimumOrderQuantity)
                throw new InvalidOperationException("Demo available balance is not sufficient for Strategy 2.");
            await runtime.Client.SetLeverageAsync(order.Symbol, order.Leverage!.Value, token);
            var positionIndex = await runtime.Client.GetPositionIndexAsync(
                order.Symbol, order.Direction == "Long" ? "Buy" : "Sell", token);
            var result = await runtime.Client.PlaceLimitOrderAsync(
                order.ToPreview(), order.OrderLinkId, positionIndex, token);
            order.BybitOrderId = result.OrderId;
            order.Status = "Submitted";
            order.SubmittedAtUtc = DateTime.UtcNow;
            order.Error = null;
            await telegram.EnteredAsync(order, token);
        }
        catch (Exception exception)
        {
            order.Status = "Error";
            order.Error = exception.Message;
            logger.LogError(exception, "Strategy 2 order {OrderLinkId} failed", order.OrderLinkId);
            await telegram.ErrorAsync(order, token);
        }
        await runtime.Store.UpdateAsync(order, token);
    }

    private async Task SynchronizeAsync(ServerSignal order, decimal price, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(order.BybitOrderId)) return;
        var status = await runtime.Client.GetOrderStatusAsync(order.BybitOrderId, token);
        if (status is null) return;
        order.AverageFillPrice = status.AveragePrice;
        order.ExecutedQuantity = status.ExecutedQuantity;
        if (status.Status == "Filled")
        {
            order.Status = "Filled";
            order.FilledAtUtc = status.UpdatedAtUtc ?? DateTime.UtcNow;
            await runtime.Store.UpdateAsync(order, token);
            await TrackLevelsAsync(order, price, token);
            return;
        }
        if (status.Status is "Cancelled" or "Rejected" or "Deactivated") order.Status = status.Status;
        await runtime.Store.UpdateAsync(order, token);
    }

    private async Task TrackLevelsAsync(ServerSignal order, decimal price, CancellationToken token)
    {
        if (order.CompletedAtUtc is not null) return;
        BackfillLevels(order);
        if (!string.IsNullOrWhiteSpace(order.StopLoss2OrderId))
        {
            var sl2 = await runtime.Client.GetOrderStatusAsync(order.StopLoss2OrderId, token);
            if (sl2?.Status == "Filled")
            {
                Complete(order, "RiskFree");
                await telegram.ClosedRiskFreeAsync(order, token);
                await runtime.Store.UpdateAsync(order, token);
                return;
            }
        }
        if (DemoOrderWorker.TargetReached(order, price))
        {
            Complete(order, "Target");
            await CancelSl2Async(order, token);
            await telegram.TargetAsync(order, token);
        }
        else if (order.RiskFreeReachedAtUtc is null && order.RiskFreePrice is { } rf &&
                 order.StopLoss2 is { } sl2 && ProfitReached(order, price, rf))
        {
            try
            {
                var rules = await runtime.Client.GetInstrumentRulesAsync(order.Symbol, token);
                var positionIndex = await runtime.Client.GetPositionIndexAsync(order.Symbol,
                    order.Direction == "Long" ? "Buy" : "Sell", token);
                var result = await runtime.Client.PlaceStopLimitAsync(order.Symbol, order.Direction,
                    order.ExecutedQuantity ?? order.Quantity, sl2, rules.TickSize,
                    $"s2sl-{order.Id:N}"[..36], positionIndex, token);
                order.StopLoss2 = result.Price;
                order.StopLoss2OrderId = result.OrderId;
                order.RiskFreeReachedAtUtc = DateTime.UtcNow;
                await telegram.RiskFreeAsync(order, token);
            }
            catch (Exception exception)
            {
                order.Error = "SL2: " + exception.Message;
                logger.LogError(exception, "Strategy 2 SL2 failed");
            }
        }
        if (DemoOrderWorker.StopLossReached(order, price))
        {
            Complete(order, "StopLoss");
            await CancelSl2Async(order, token);
            await telegram.StopAsync(order, token);
        }
        await runtime.Store.UpdateAsync(order, token);
    }

    private async Task CancelSl2Async(ServerSignal order, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(order.StopLoss2OrderId)) return;
        try { await runtime.Client.CancelOrderAsync(order.Symbol, order.StopLoss2OrderId, token); }
        catch (Exception exception) { logger.LogWarning(exception, "Strategy 2 SL2 cancellation failed"); }
    }

    private static bool IsActive(ServerSignal x) => x.Status is "Pending" or "Submitted" or "Filled";
    private static bool InitialExpiryReached(ServerSignal x, decimal price) => x.Direction switch
    {
        "Long" => price >= x.Ceiling, "Short" => price <= x.Floor, _ => false
    };
    private static bool ProfitReached(ServerSignal x, decimal price, decimal level) => x.Direction switch
    {
        "Long" => price >= level, "Short" => price <= level, _ => false
    };
    private static void BackfillLevels(ServerSignal x)
    {
        var distance = x.TakeProfit - x.EntryPrice;
        if (distance == 0) return;
        x.StopLoss2 ??= x.EntryPrice + distance * 0.25m;
        x.RiskFreePrice ??= x.EntryPrice + distance * 0.75m;
    }
    private static void Complete(ServerSignal x, string outcome, string? reason = null)
    {
        if (x.CompletedAtUtc is not null) return;
        var now = DateTime.UtcNow;
        x.Outcome = outcome;
        x.CompletedAtUtc = now;
        x.Status = outcome == "Expired" ? "Expired" : "Completed";
        x.ExpireReason = reason;
        if (outcome == "Target") x.TargetReachedAtUtc = now;
        else if (outcome == "RiskFree") x.RiskFreeClosedAtUtc = now;
        else if (outcome == "StopLoss") x.StopLossReachedAtUtc = now;
        else if (outcome == "Expired") x.ExpiredAtUtc = now;
    }
}

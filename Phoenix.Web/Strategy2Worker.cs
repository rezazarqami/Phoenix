using Phoenix.Engine.Exchanges.Bybit;
using System.Threading.Channels;

namespace Phoenix.Web;

public sealed class Strategy2Worker(
    Strategy2Runtime runtime,
    Strategy2TelegramNotifier telegram,
    ILogger<Strategy2Worker> logger) : BackgroundService
{
    // Persist claims before network work; the live trade reader must never wait for Bybit/Telegram.
    private readonly Channel<ServerSignal> submissions = Channel.CreateUnbounded<ServerSignal>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<ServerSignal> expirations = Channel.CreateUnbounded<ServerSignal>(
        new UnboundedChannelOptions { SingleReader = true });

    public async Task ObservePriceAsync(Guid id, decimal price, bool allowExpiry, CancellationToken token)
    {
        ServerSignal? claimed = null;
        ServerSignal? expired = null;
        await runtime.Store.ExecutionGate.WaitAsync(token);
        try
        {
            var order = (await runtime.Store.GetAllAsync(token)).SingleOrDefault(x => x.Id == id);
            if (order is null || order.Status != "Pending" || order.CompletedAtUtc is not null) return;
            // Recovery is an explicit trading pause, never evidence of a missed/expired entry.
            if (runtime.Store.EntriesPaused) return;
            order.LastPrice = price;
            // An observed entry always wins over expiry evaluation for the same price.
            if (DemoOrderWorker.EntryReached(order, price))
            {
                var result = await runtime.Store.TryClaimExclusiveSubmissionAsync(id, price, token);
                if (result == ExclusiveClaimResult.Claimed)
                    claimed = (await runtime.Store.GetAllAsync(token)).Single(x => x.Id == id);
                else if (result == ExclusiveClaimResult.PositionBusy)
                {
                    Complete(order, "Expired", "PositionAlreadyOpen");
                    await runtime.Store.UpdateAsync(order, token);
                    expired = order;
                }
            }
            else if (allowExpiry)
            {
                if (order.ExpirePrice == 0) order.ExpirePrice = order.Direction == "Long" ? order.Ceiling : order.Floor;
                if (order.ExpireActivationPrice == 0)
                    order.ExpireActivationPrice = order.EntryPrice + 0.25m * (order.TakeProfit - order.EntryPrice);
                if (order.ExpireStage == "Initial" && InitialExpiryReached(order, price))
                    Complete(order, "Expired", "InitialBoundary");
                else
                {
                    if (order.ExpireStage == "Initial" && DemoOrderWorker.ExpireActivationReached(order, price))
                    {
                        order.ExpireStage = "Target";
                        order.ExpirePrice = order.TakeProfit;
                        order.ExpireAdjustedAtUtc = DateTime.UtcNow;
                    }
                    if (order.ExpireStage == "Target" && DemoOrderWorker.TargetExpiryReached(order, price))
                        Complete(order, "Expired", "TargetAfterActivation");
                }
                await runtime.Store.UpdateAsync(order, token);
                if (order.Status == "Expired") expired = order;
            }
        }
        finally { runtime.Store.ExecutionGate.Release(); }
        if (claimed is not null) submissions.Writer.TryWrite(claimed);
        if (expired is not null) expirations.Writer.TryWrite(expired);
    }

    private async Task SubmitQueueAsync(CancellationToken token)
    {
        await foreach (var order in submissions.Reader.ReadAllAsync(token))
        {
            try { await SubmitAsync(order, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Strategy 2 dispatch failed for {Signal}", order.Id); }
        }
    }

    private async Task NotifyExpiryQueueAsync(CancellationToken token)
    {
        // A slow Telegram expiry message must never delay an actual entry order.
        await foreach (var order in expirations.Reader.ReadAllAsync(token))
        {
            try { await telegram.ExpiredAsync(order, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogWarning(exception, "Strategy 2 expiry notification failed for {Signal}", order.Id); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sender = SubmitQueueAsync(linked.Token);
        var notifier = NotifyExpiryQueueAsync(linked.Token);
        try
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
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(sender, notifier); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task ProcessCycleAsync(CancellationToken token)
    {
        var orders = await runtime.Store.GetAllAsync(token);
        foreach (var group in orders.Where(IsActive).GroupBy(x => x.Symbol))
        {
            try
            {
                var price = (await runtime.Client.GetLastPriceAsync(group.Key, token)).LastPrice;
                foreach (var order in group)
                {
                    order.LastPrice = price;
                    if (order.Status == "Pending") await ObservePriceAsync(order.Id, price, true, token);
                    else if (order.Status == "Submitted") await SynchronizeAsync(order, price, token);
                    else if (order.Status == "Filled") await TrackLevelsAsync(order, price, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogWarning(exception, "Strategy 2 symbol {Symbol} failed; continuing", group.Key); }
        }
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
            order.ApplyPhoenixLeverage(rules, 20m);
            if (order.PositionSizeUsdt <= 0 || order.Quantity < rules.MinimumOrderQuantity)
                throw new InvalidOperationException("Demo available balance is not sufficient for Strategy 2.");
            await runtime.Client.SetLeverageAsync(order.Symbol, order.Leverage!.Value, token);
            var positionIndex = await runtime.Client.GetPositionIndexAsync(
                order.Symbol, order.Direction == "Long" ? "Buy" : "Sell", token);
            var result = await runtime.Client.PlaceMarketOrderAsync(
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
        var filled = await new StagedRiskProtection(runtime.Client, runtime.Store).FilledAsync(order, token);
        if (filled is not null)
        {
            Complete(order, "RiskFree");
            await CancelSl2Async(order, token, filled.OrderId);
            await telegram.ClosedRiskFreeAsync(order, token);
            await runtime.Store.UpdateAsync(order, token);
            return;
        }
        if (DemoOrderWorker.TargetReached(order, price))
        {
            Complete(order, "Target");
            await CancelSl2Async(order, token);
            await telegram.TargetAsync(order, token);
        }
        else
        {
            try
            {
                if (await new StagedRiskProtection(runtime.Client, runtime.Store).AdvanceAsync(order, price, token))
                    await telegram.RiskFreeAsync(order, token);
            }
            catch (Exception exception)
            {
                order.Error = "Risk-free protection: " + exception.Message;
                logger.LogError(exception, "Strategy 2 protection failed");
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

    private async Task CancelSl2Async(ServerSignal order, CancellationToken token, string? exceptOrderId = null)
    {
        foreach (var id in StagedRiskProtection.OrderIds(order).Where(id => id != exceptOrderId))
            try { await runtime.Client.CancelOrderAsync(order.Symbol, id, token); }
            catch (Exception exception) { logger.LogWarning(exception, "Strategy 2 protection cancellation failed"); }
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
        if (x.RiskFreeReachedAtUtc is null && x.StopLoss2OrderId is null)
            x.StopLoss2 = x.EntryPrice + distance * 0.50m;
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

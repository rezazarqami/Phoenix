using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

// Shared by both trading strategies. Persist each accepted order before the next step.
public sealed class StagedRiskProtection(BybitDemoClient client, ServerOrderStore store)
{
    public static decimal Level(ServerSignal order, decimal fraction) =>
        order.EntryPrice + (order.TakeProfit - order.EntryPrice) * fraction;

    public static IEnumerable<string> OrderIds(ServerSignal order) =>
        new[] { order.EarlyStopLimitOrderId, order.StopLoss2OrderId, order.RiskFreeStopMarketOrderId }
            .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Distinct();

    private static bool Reached(ServerSignal order, decimal price, decimal fraction) =>
        order.Direction == "Long" ? price >= Level(order, fraction) : price <= Level(order, fraction);

    public async Task<BybitOrderStatus?> FilledAsync(ServerSignal order, CancellationToken token)
    {
        foreach (var id in OrderIds(order))
        {
            var status = await client.GetOrderStatusAsync(id, token);
            if (status?.Status == "Filled")
            {
                RiskFreeExit.Record(order,status.OrderId);
                return status;
            }
        }
        return null;
    }

    public async Task<bool> AdvanceAsync(ServerSignal order, decimal price, CancellationToken token)
    {
        if (order.CompletedAtUtc is not null) return false;
        var stage = order.RiskFreeReachedAtUtc is not null || Reached(order, price, .75m) ? 2
            : Reached(order, price, .50m) ? 1 : 0;
        // A failed request or a restart must not forget a threshold already observed.
        order.ProtectionStage = Math.Max(order.ProtectionStage, stage);
        if (order.ProtectionStage == 0) return false;
        await store.UpdateAsync(order, token);
        if (order.ProtectionStage == 1 && !string.IsNullOrWhiteSpace(order.EarlyStopLimitOrderId)) return false;
        if (order.ProtectionStage == 2 && order.RiskFreeReachedAtUtc is not null &&
            !string.IsNullOrWhiteSpace(order.StopLoss2OrderId) &&
            !string.IsNullOrWhiteSpace(order.RiskFreeStopMarketOrderId) &&
            (string.IsNullOrWhiteSpace(order.EarlyStopLimitOrderId) || order.EarlyStopLimitCancelledAtUtc is not null))
            return false;

        var rules = await client.GetInstrumentRulesAsync(order.Symbol, token);
        var positionIndex = await client.GetPositionIndexAsync(order.Symbol,
            order.Direction == "Long" ? "Buy" : "Sell", token);
        var quantity = order.ExecutedQuantity ?? order.Quantity;
        if (order.ProtectionStage == 1)
        {
            if (string.IsNullOrWhiteSpace(order.EarlyStopLimitOrderId))
            {
                var result = await PlaceAsync(order, quantity, Level(order, .20m), rules.TickSize,
                    $"early-{order.Id:N}"[..36], positionIndex, false, token);
                order.EarlyStopLimit = result.Price;
                order.EarlyStopLimitOrderId = result.OrderId;
                order.EarlyProtectionAtUtc ??= DateTime.UtcNow;
                await store.UpdateAsync(order, token);
            }
            return false;
        }

        if (string.IsNullOrWhiteSpace(order.StopLoss2OrderId))
        {
            var result = await PlaceAsync(order, quantity, Level(order, .50m), rules.TickSize,
                $"sl2-{order.Id:N}"[..36], positionIndex, false, token);
            order.StopLoss2 = result.Price;
            order.StopLoss2OrderId = result.OrderId;
            await store.UpdateAsync(order, token);
        }
        if (string.IsNullOrWhiteSpace(order.RiskFreeStopMarketOrderId))
        {
            var result = await PlaceAsync(order, quantity, Level(order, .20m), rules.TickSize,
                $"rfm-{order.Id:N}"[..36], positionIndex, true, token);
            order.RiskFreeStopMarket = result.Price;
            order.RiskFreeStopMarketOrderId = result.OrderId;
            await store.UpdateAsync(order, token);
        }
        // Establish the replacement first, then confirm cancellation of the early limit.
        if (!string.IsNullOrWhiteSpace(order.EarlyStopLimitOrderId) && order.EarlyStopLimitCancelledAtUtc is null)
        {
            await CancelEarlyAsync(order, token);
            order.EarlyStopLimitCancelledAtUtc = DateTime.UtcNow;
            await store.UpdateAsync(order, token);
        }
        var newlyReached = order.RiskFreeReachedAtUtc is null;
        order.RiskFreeReachedAtUtc ??= DateTime.UtcNow;
        await store.UpdateAsync(order, token);
        return newlyReached;
    }

    private async Task CancelEarlyAsync(ServerSignal order, CancellationToken token)
    {
        var id = order.EarlyStopLimitOrderId!;
        var status = await client.GetOrderStatusAsync(id, token);
        if (status?.Status is "Cancelled" or "Deactivated" or "Rejected") return;
        if (status?.ExecutedQuantity > 0)
            throw new InvalidOperationException("Early protection executed during upgrade; reconcile before continuing.");
        try { await client.CancelOrderAsync(order.Symbol, id, token); }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            status = await client.GetOrderStatusAsync(id, token);
            if (status?.Status is "Cancelled" or "Deactivated" or "Rejected") return;
            throw;
        }
        status = await client.GetOrderStatusAsync(id, token);
        if (status?.Status is not ("Cancelled" or "Deactivated" or "Rejected") || status.ExecutedQuantity > 0)
            throw new InvalidOperationException("Early protection cancellation is not confirmed; retry reconciliation.");
    }

    private async Task<BybitOrderResult> PlaceAsync(ServerSignal order, decimal quantity, decimal price,
        decimal tickSize, string linkId, int positionIndex, bool market, CancellationToken token)
    {
        try
        {
            return market
                ? await client.PlaceStopMarketAsync(order.Symbol, order.Direction, quantity, price,
                    tickSize, linkId, positionIndex, token)
                : await client.PlaceStopLimitAsync(order.Symbol, order.Direction, quantity, price,
                    tickSize, linkId, positionIndex, token);
        }
        catch (Exception exception) when (exception.Message.Contains("110072", StringComparison.Ordinal) ||
                                         exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            var recovered = await client.GetOrderStatusByLinkIdAsync(linkId, token);
            if (recovered is null || recovered.Status is "Cancelled" or "Rejected" or "Deactivated") throw;
            return new BybitOrderResult(recovered.OrderId, linkId, order.Symbol,
                order.Direction == "Long" ? "Sell" : "Buy", quantity,
                BybitOrderPreviewBuilder.RoundToStep(price, tickSize));
        }
    }
}

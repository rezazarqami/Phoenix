using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class DemoOrderWorker(
    BybitDemoClient client,
    BybitDemoOptions options,
    ServerState state,
    ServerOrderStore store,
    TelegramNotifier telegram,
    ILogger<DemoOrderWorker> logger) : BackgroundService
{
    public static bool IsTradingEnabled(BybitDemoOptions options) =>
        options.HasCredentials && string.Equals(
            Environment.GetEnvironmentVariable("PHOENIX_DEMO_TRADING_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var orders = await store.GetAllAsync(stoppingToken);
                var symbols = orders.Where(x => x.Status is "Pending" or "Submitted" or "Filled").Select(x => x.Symbol)
                    .Append("BTCUSDT").Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var symbol in symbols)
                {
                    var ticker = await client.GetLastPriceAsync(symbol, stoppingToken);
                    if (symbol == "BTCUSDT")
                    {
                        state.LastPrice = ticker.LastPrice;
                        state.LastUpdatedUtc = DateTime.UtcNow;
                    }
                    foreach (var order in orders.Where(x =>
                                 (x.Status is "Pending" or "Submitted" or "Filled") && x.Symbol == symbol))
                    {
                        order.LastPrice = ticker.LastPrice;
                        if (order.Status == "Pending")
                        {
                            if (EntryReached(order, ticker.LastPrice) && IsTradingEnabled(options) &&
                                await store.TryClaimSubmissionAsync(order.Id, ticker.LastPrice, stoppingToken))
                                await SubmitAsync(order, stoppingToken);
                            else
                                await TrackPendingExpiryAsync(order, ticker.LastPrice, stoppingToken);
                        }
                        else if (order.Status == "Submitted")
                            await SynchronizeOrderAsync(order, ticker.LastPrice, stoppingToken);
                        else if (order.Status == "Filled")
                            await TrackLevelsAsync(order, ticker.LastPrice, stoppingToken);
                        else
                            await store.UpdateAsync(order, stoppingToken);
                    }
                }
                state.PublicApiConnected = true;
                state.Error = null;

                if (options.HasCredentials && !state.DemoAuthenticated)
                    state.DemoAuthenticated = (await client.CheckConnectionAsync(stoppingToken)).Authenticated;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                state.PublicApiConnected = false;
                state.DemoAuthenticated = false;
                state.Error = exception.Message;
                logger.LogWarning(exception, "Phoenix Demo worker cycle failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task TrackPendingExpiryAsync(ServerSignal order, decimal price, CancellationToken token)
    {
        if (order.ExpirePrice == 0)
            order.ExpirePrice = order.Direction == "Long" ? order.Ceiling : order.Floor;
        if (order.ExpireActivationPrice == 0)
            order.ExpireActivationPrice = order.EntryPrice + 0.20m * (order.TakeProfit - order.EntryPrice);

        if (order.ExpireStage == "Initial" && InitialExpiryReached(order, price))
        {
            order.Status = "Expired";
            order.ExpiredAtUtc = DateTime.UtcNow;
            await store.UpdateAsync(order, token);
            return;
        }

        if (order.ExpireStage == "Initial" && ExpireActivationReached(order, price))
        {
            order.ExpireStage = "Target";
            order.ExpirePrice = order.TakeProfit;
            order.ExpireAdjustedAtUtc = DateTime.UtcNow;
        }

        if (order.ExpireStage == "Target" && TargetExpiryReached(order, price))
        {
            order.Status = "Expired";
            order.ExpiredAtUtc = DateTime.UtcNow;
        }

        await store.UpdateAsync(order, token);
    }

    private static bool InitialExpiryReached(ServerSignal order, decimal price) => order.Direction switch
    {
        "Long" => price >= order.Ceiling,
        "Short" => price <= order.Floor,
        _ => false
    };

    public static bool ExpireActivationReached(ServerSignal order, decimal price) => order.Direction switch
    {
        "Long" => price <= order.ExpireActivationPrice,
        "Short" => price >= order.ExpireActivationPrice,
        _ => false
    };

    public static bool TargetExpiryReached(ServerSignal order, decimal price) => order.Direction switch
    {
        "Long" => price >= order.TakeProfit,
        "Short" => price <= order.TakeProfit,
        _ => false
    };

    private async Task SynchronizeOrderAsync(ServerSignal order, decimal price, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(order.BybitOrderId)) return;
        var status = await client.GetOrderStatusAsync(order.BybitOrderId, token);
        if (status is null) return;

        order.AverageFillPrice = status.AveragePrice;
        order.ExecutedQuantity = status.ExecutedQuantity;
        if (status.Status == "Filled")
        {
            order.Status = "Filled";
            order.FilledAtUtc = status.UpdatedAtUtc ?? DateTime.UtcNow;
            await store.UpdateAsync(order, token);
            await TrackLevelsAsync(order, price, token);
            return;
        }
        if (status.Status is "Cancelled" or "Rejected" or "Deactivated")
        {
            order.Status = status.Status;
            await store.UpdateAsync(order, token);
            return;
        }
        await store.UpdateAsync(order, token);
    }

    private async Task SubmitAsync(ServerSignal order, CancellationToken token)
    {
        order.Status = "Submitting";
        order.Error = null;
        await store.UpdateAsync(order, token);
        await telegram.EntryReachedAsync(order, token);
        try
        {
            var result = await client.PlaceLimitOrderAsync(order.ToPreview(), order.OrderLinkId, token);
            order.BybitOrderId = result.OrderId;
            order.Status = "Submitted";
            order.SubmittedAtUtc = DateTime.UtcNow;
            await telegram.OrderSubmittedAsync(order, token);
        }
        catch (Exception exception)
        {
            order.Status = "Error";
            order.Error = exception.Message;
            logger.LogError(exception, "Demo order {OrderLinkId} was not confirmed", order.OrderLinkId);
            await telegram.OrderErrorAsync(order, token);
        }
        await store.UpdateAsync(order, token);
    }

    private async Task TrackLevelsAsync(ServerSignal order, decimal price, CancellationToken token)
    {
        BackfillStopLoss2Levels(order);
        if (order.StopLossReachedAtUtc is null && !string.IsNullOrWhiteSpace(order.StopLoss2OrderId))
        {
            var sl2Status = await client.GetOrderStatusAsync(order.StopLoss2OrderId, token);
            if (sl2Status?.Status == "Filled")
            {
                order.RiskFreeClosedAtUtc = sl2Status.UpdatedAtUtc ?? DateTime.UtcNow;
                await telegram.RiskFreeClosedAsync(order, token);
                await store.UpdateAsync(order, token);
                return;
            }
        }

        if (order.TargetReachedAtUtc is null && TargetReached(order, price))
        {
            order.TargetReachedAtUtc = DateTime.UtcNow;
            await CancelStopLoss2Async(order, token);
            await telegram.TargetReachedAsync(order, token);
        }
        else if (order.RiskFreeReachedAtUtc is null && order.RiskFreePrice is { } riskFree &&
                 order.StopLoss2 is { } stopLoss2 && ProfitLevelReached(order, price, riskFree))
        {
            try
            {
                var rules = await client.GetInstrumentRulesAsync(order.Symbol, token);
                var linkId = $"sl2-{order.Id:N}"[..36];
                var result = await client.PlaceStopLimitAsync(
                    order.Symbol, order.Direction, order.ExecutedQuantity ?? order.Quantity,
                    stopLoss2, rules.TickSize, linkId, token);
                order.StopLoss2 = result.Price;
                order.StopLoss2OrderId = result.OrderId;
                order.RiskFreeReachedAtUtc = DateTime.UtcNow;
                await telegram.RiskFreeReachedAsync(order, token);
            }
            catch (Exception exception)
            {
                order.Error = $"SL2: {exception.Message}";
                logger.LogError(exception, "SL2 stop-limit creation failed for {OrderLinkId}", order.OrderLinkId);
            }
        }

        if (order.StopLossReachedAtUtc is null && StopLossReached(order, price))
        {
            order.StopLossReachedAtUtc = DateTime.UtcNow;
            await CancelStopLoss2Async(order, token);
            await telegram.StopLossReachedAsync(order, token);
        }

        await store.UpdateAsync(order, token);
    }

    private static void BackfillStopLoss2Levels(ServerSignal order)
    {
        if (order.RiskFreeReachedAtUtc is not null || order.StopLoss2OrderId is not null) return;
        var distance = order.TakeProfit - order.EntryPrice;
        if (distance == 0) return;
        order.StopLoss2 = order.EntryPrice + distance * 0.25m;
        order.RiskFreePrice = order.EntryPrice + distance * 0.75m;
    }

    private async Task CancelStopLoss2Async(ServerSignal order, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(order.StopLoss2OrderId)) return;
        try { await client.CancelOrderAsync(order.Symbol, order.StopLoss2OrderId, token); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not cancel remaining SL2 order {StopLoss2OrderId}", order.StopLoss2OrderId);
        }
    }

    public static bool EntryReached(ServerSignal order, decimal price) => order.Direction switch
    {
        "Long" => price <= order.EntryPrice,
        "Short" => price >= order.EntryPrice,
        _ => false
    };

    public static bool TargetReached(ServerSignal order, decimal price) =>
        ProfitLevelReached(order, price, order.TakeProfit);

    public static bool StopLossReached(ServerSignal order, decimal price) => order.Direction switch
    {
        "Long" => price <= order.StopLoss,
        "Short" => price >= order.StopLoss,
        _ => false
    };

    private static bool ProfitLevelReached(ServerSignal order, decimal price, decimal level) => order.Direction switch
    {
        "Long" => price >= level,
        "Short" => price <= level,
        _ => false
    };
}

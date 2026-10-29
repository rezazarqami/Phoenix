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
            Environment.GetEnvironmentVariable(options.IsReal
                ? "PHOENIX_REAL_TRADING_ENABLED"
                : "PHOENIX_DEMO_TRADING_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var orders = await store.GetAllAsync(stoppingToken);
                var symbols = orders.Where(x => x.Status is "Pending" or "Submitted" or "Filled").Select(x => x.Symbol)
                    .Append("BTCUSDT").Distinct(StringComparer.OrdinalIgnoreCase);
                string? marketError=null;
                foreach (var symbol in symbols)
                {
                    try
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
                        ServerSignal? claimed = null;
                        await store.ExecutionGate.WaitAsync(stoppingToken);
                        try
                        {
                        order.LastPrice = ticker.LastPrice;
                        var latest = (await store.GetAllAsync(stoppingToken)).SingleOrDefault(x => x.Id == order.Id);
                        if (latest is null || latest.Status != order.Status || latest.CompletedAtUtc is not null) continue;
                        if (order.Status == "Pending")
                        {
                            var entryReached = EntryReached(order, ticker.LastPrice);
                            if (entryReached && IsTradingEnabled(options))
                            {
                                if (await store.TryClaimSubmissionAsync(order.Id, ticker.LastPrice, stoppingToken))
                                    claimed = (await store.GetAllAsync(stoppingToken)).Single(x => x.Id == order.Id);
                                // Another entry worker already claimed it. Never write this stale Pending copy back.
                            }
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
                        finally { store.ExecutionGate.Release(); }
                        // Never keep entry detection locked while talking to Bybit or Telegram.
                        if (claimed is not null) await SubmitAsync(claimed, stoppingToken);
                    }
                    }
                    catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){throw;}
                    catch(Exception exception)
                    {
                        marketError=exception.Message;
                        logger.LogWarning(exception,"Phoenix monitoring failed for {Symbol}; other symbols continue",symbol);
                    }
                }
                state.PublicApiConnected = marketError is null;
                state.Error = marketError;

                if (options.HasCredentials && !state.DemoAuthenticated)
                    state.DemoAuthenticated = (await client.CheckConnectionAsync(stoppingToken)).Authenticated;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                state.PublicApiConnected = false;
                state.DemoAuthenticated = false;
                state.Error = exception.Message;
                logger.LogWarning(exception, "Phoenix {Environment} worker cycle failed", options.EnvironmentName);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task TrackPendingExpiryAsync(ServerSignal order, decimal price, CancellationToken token)
    {
        if (order.ExpirePrice == 0)
            order.ExpirePrice = order.Direction == "Long" ? order.Ceiling : order.Floor;
        if (order.ExpireActivationPrice == 0)
            order.ExpireActivationPrice = order.EntryPrice + 0.25m * (order.TakeProfit - order.EntryPrice);

        if (order.ExpireStage == "Initial" && InitialExpiryReached(order, price))
        {
            Complete(order, "Expired", DateTime.UtcNow, "InitialBoundary");
            await store.UpdateAsync(order, token);
            return;
        }

        if (order.ExpireStage == "Initial" && ExpireActivationReached(order, price))
        {
            order.ExpireStage = "Target";
            order.ExpirePrice = order.TakeProfit;
            order.ExpireAdjustedAtUtc = DateTime.UtcNow;
            ExpiryEvidence.Record(order, price, order.ExpireAdjustedAtUtc.Value);
            order.PublicSignalNumber = await store.ReservePublicSignalNumberAsync(order.Id, token);
            // Persist the activation immediately and never block entry detection on
            // multi-timeframe analysis, chart rendering, or Telegram delivery.
            await store.UpdateAsync(order, token);
            // The durable notification worker publishes/retries from this persisted activation.
        }

        if (order.ExpireStage == "Target")
        {
            var reached = TargetExpiryReached(order, price);
            ExpiryEvidence.Record(order, price, DateTime.UtcNow, reached);
            if (reached) Complete(order, "Expired", DateTime.UtcNow, "TargetAfterActivation");
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
        try
        {
            if (order.LeverageSource is not ("PhoenixFormula" or "Manual"))
            {
                order.ApplyPhoenixLeverage(await client.GetInstrumentRulesAsync(order.Symbol, token));
                await store.UpdateAsync(order, token);
            }
            await client.SetLeverageAsync(order.Symbol, order.Leverage
                ?? throw new InvalidOperationException("Signal leverage is missing."), token);
            var positionIndex = await client.GetPositionIndexAsync(order.Symbol,
                order.Direction == "Long" ? "Buy" : "Sell", token);
            var result = await client.PlaceMarketOrderAsync(
                order.ToPreview(), order.OrderLinkId, positionIndex, token);
            order.BybitOrderId = result.OrderId;
            order.Status = "Submitted";
            order.SubmittedAtUtc = DateTime.UtcNow;
            await telegram.OrderSubmittedAsync(order, token);
        }
        catch (Exception exception)
        {
            var recovered = exception.Message.Contains("110072", StringComparison.Ordinal) ||
                            exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                ? await client.GetOrderStatusByLinkIdAsync(order.OrderLinkId, token) : null;
            if (recovered is not null)
            {
                order.BybitOrderId = recovered.OrderId;
                order.Status = recovered.Status == "Filled" ? "Filled" : "Submitted";
                if (order.Status == "Filled") order.FilledAtUtc = recovered.UpdatedAtUtc ?? DateTime.UtcNow;
                order.Error = null;
            }
            else
            {
                order.Status = "Error";
                order.Error = exception.Message;
                logger.LogError(exception, "{Environment} order {OrderLinkId} was not confirmed",
                    options.EnvironmentName, order.OrderLinkId);
                await telegram.OrderErrorAsync(order, token);
            }
        }
        await store.UpdateAsync(order, token);
    }

    private async Task TrackLevelsAsync(ServerSignal order, decimal price, CancellationToken token)
    {
        if (order.CompletedAtUtc is not null) return;
        BackfillStopLoss2Levels(order);
        var filledProtection = await new StagedRiskProtection(client, store).FilledAsync(order, token);
        if (filledProtection is not null)
        {
            Complete(order, "RiskFree", filledProtection.UpdatedAtUtc ?? DateTime.UtcNow);
            await store.UpdateAsync(order, token);
            await CancelRiskFreeProtectionAsync(order, token, filledProtection.OrderId);
            await WalletNotification.CaptureAsync(client, store, order, token);
            await telegram.RiskFreeClosedAsync(order, token);
            return;
        }

        if (order.TargetReachedAtUtc is null && TargetReached(order, price))
        {
            Complete(order, "Target", DateTime.UtcNow);
            await store.UpdateAsync(order, token);
            await CancelRiskFreeProtectionAsync(order, token);
            await WalletNotification.CaptureAsync(client, store, order, token);
            await telegram.TargetReachedAsync(order, token);
        }
        else
        {
            try
            {
                if (await new StagedRiskProtection(client, store).AdvanceAsync(order, price, token))
                    await telegram.RiskFreeReachedAsync(order, token);
            }
            catch (Exception exception)
            {
                order.Error = $"Risk-free protection: {exception.Message}";
                logger.LogError(exception, "Risk-free protection creation failed for {OrderLinkId}", order.OrderLinkId);
            }
        }

        if (order.StopLossReachedAtUtc is null && StopLossReached(order, price))
        {
            Complete(order, "StopLoss", DateTime.UtcNow);
            order.FailureReason = ProfessionalSignalAnalysisService.ExplainStop(order.TechnicalFeatures);
            await store.UpdateAsync(order, token);
            await CancelRiskFreeProtectionAsync(order, token);
            await WalletNotification.CaptureAsync(client, store, order, token);
            await telegram.StopLossReachedAsync(order, token);
        }

        await store.UpdateAsync(order, token);
    }

    private static void Complete(ServerSignal order, string outcome, DateTime at, string? expireReason = null)
    {
        if (order.CompletedAtUtc is not null) return;
        order.Outcome = outcome;
        order.CompletedAtUtc = at;
        order.Status = outcome == "Expired" ? "Expired" : "Completed";
        order.ExpireReason = expireReason;
        if (outcome == "Target") order.TargetReachedAtUtc = at;
        else if (outcome == "RiskFree") order.RiskFreeClosedAtUtc = at;
        else if (outcome == "StopLoss") order.StopLossReachedAtUtc = at;
        else if (outcome == "Expired") order.ExpiredAtUtc = at;
    }

    private static void BackfillStopLoss2Levels(ServerSignal order)
    {
        var distance = order.TakeProfit - order.EntryPrice;
        if (distance == 0) return;
        order.StopLoss2 ??= order.EntryPrice + distance * 0.50m;
        order.RiskFreeStopMarket ??= RiskFreeStopMarketPrice(order.EntryPrice, order.TakeProfit);
        order.RiskFreePrice ??= order.EntryPrice + distance * 0.75m;
    }

    public static decimal RiskFreeStopMarketPrice(decimal entryPrice, decimal takeProfit) =>
        entryPrice + (takeProfit - entryPrice) * 0.20m;

    private async Task CancelRiskFreeProtectionAsync(
        ServerSignal order, CancellationToken token, string? exceptOrderId = null)
    {
        foreach (var orderId in StagedRiskProtection.OrderIds(order)
                     .Where(value => !string.IsNullOrWhiteSpace(value) && value != exceptOrderId))
            try { await client.CancelOrderAsync(order.Symbol, orderId!, token); }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not cancel remaining risk-free order {OrderId}", orderId);
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

using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class DemoOrderWorker(
    BybitDemoClient client,
    BybitDemoOptions options,
    ServerState state,
    ServerOrderStore store,
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
                var symbols = orders.Where(x => x.Status == "Pending").Select(x => x.Symbol)
                    .Append("BTCUSDT").Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var symbol in symbols)
                {
                    var ticker = await client.GetLastPriceAsync(symbol, stoppingToken);
                    if (symbol == "BTCUSDT")
                    {
                        state.LastPrice = ticker.LastPrice;
                        state.LastUpdatedUtc = DateTime.UtcNow;
                    }
                    foreach (var order in orders.Where(x => x.Status == "Pending" && x.Symbol == symbol))
                    {
                        order.LastPrice = ticker.LastPrice;
                        if (EntryReached(order, ticker.LastPrice) && IsTradingEnabled(options))
                            await SubmitAsync(order, stoppingToken);
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

    private async Task SubmitAsync(ServerSignal order, CancellationToken token)
    {
        order.Status = "Submitting";
        order.Error = null;
        await store.UpdateAsync(order, token);
        try
        {
            var result = await client.PlaceLimitOrderAsync(order.ToPreview(), order.OrderLinkId, token);
            order.BybitOrderId = result.OrderId;
            order.Status = "Submitted";
            order.SubmittedAtUtc = DateTime.UtcNow;
        }
        catch (Exception exception)
        {
            order.Status = "Error";
            order.Error = exception.Message;
            logger.LogError(exception, "Demo order {OrderLinkId} was not confirmed", order.OrderLinkId);
        }
        await store.UpdateAsync(order, token);
    }

    private static bool EntryReached(ServerSignal order, decimal price) => order.Direction switch
    {
        "Long" => price <= order.EntryPrice,
        "Short" => price >= order.EntryPrice,
        _ => false
    };
}

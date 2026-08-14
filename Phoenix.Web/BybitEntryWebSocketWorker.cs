using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class BybitEntryWebSocketWorker(
    BybitDemoClient client,
    BybitDemoOptions options,
    ServerOrderStore store,
    TelegramNotifier telegram,
    ILogger<BybitEntryWebSocketWorker> logger) : BackgroundService
{
    private static readonly Uri StreamUri = new("wss://stream.bybit.com/v5/public/linear");
    private readonly ConcurrentDictionary<Guid, EntryWatch> _pending = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(StreamUri, stoppingToken);
                var subscribed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var subscriptions = MaintainSubscriptionsAsync(socket, subscribed, linked.Token);
                await ReceiveTradesAsync(socket, linked.Token);
                linked.Cancel();
                await IgnoreCancellationAsync(subscriptions);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Bybit entry WebSocket disconnected; reconnecting");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task MaintainSubscriptionsAsync(ClientWebSocket socket, HashSet<string> subscribed,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var pending = await store.GetAllAsync(token);
            var active = pending.Where(x => x.Status == "Pending").ToArray();
            var activeIds = active.Select(x => x.Id).ToHashSet();
            foreach (var stale in _pending.Keys.Where(x => !activeIds.Contains(x)))
                _pending.TryRemove(stale, out _);
            foreach (var order in active)
                _pending[order.Id] = new(order.Id, order.Symbol, order.Direction, order.EntryPrice);

            var additions = active.Select(x => x.Symbol)
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(subscribed.Add).ToArray();
            if (additions.Length > 0)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    op = "subscribe",
                    args = additions.Select(x => $"publicTrade.{x}").ToArray()
                });
                await socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, token);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    private async Task ReceiveTradesAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            using var document = JsonDocument.Parse(message.ToArray());
            if (!document.RootElement.TryGetProperty("topic", out var topic) ||
                !topic.GetString()!.StartsWith("publicTrade.", StringComparison.Ordinal) ||
                !document.RootElement.TryGetProperty("data", out var data)) continue;

            foreach (var trade in data.EnumerateArray())
            {
                var symbol = trade.GetProperty("s").GetString();
                var text = trade.GetProperty("p").GetString();
                if (symbol is not null && decimal.TryParse(text, NumberStyles.Number,
                        CultureInfo.InvariantCulture, out var price))
                    await HandleTradeAsync(symbol, price, token);
            }
        }
    }

    private async Task HandleTradeAsync(string symbol, decimal price, CancellationToken token)
    {
        if (!DemoOrderWorker.IsTradingEnabled(options)) return;
        var candidates = _pending.Values.Where(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        foreach (var watch in candidates)
        {
            if (!EntryReached(watch, price) || !await store.TryClaimSubmissionAsync(watch.Id, price, token))
                continue;
            _pending.TryRemove(watch.Id, out _);
            var order = (await store.GetAllAsync(token)).Single(x => x.Id == watch.Id);
            await SubmitClaimedAsync(order, token);
        }
    }

    private async Task SubmitClaimedAsync(ServerSignal order, CancellationToken token)
    {
        order.Status = "Submitting";
        await telegram.EntryReachedAsync(order, token);
        try
        {
            if (order.LeverageSource != "PhoenixFormula")
            {
                order.ApplyPhoenixLeverage(await client.GetInstrumentRulesAsync(order.Symbol, token));
                await store.UpdateAsync(order, token);
            }
            await client.SetLeverageAsync(order.Symbol, order.Leverage
                ?? throw new InvalidOperationException("Signal leverage is missing."), token);
            var result = await client.PlaceLimitOrderAsync(order.ToPreview(), order.OrderLinkId, token);
            order.BybitOrderId = result.OrderId;
            order.Status = "Submitted";
            order.SubmittedAtUtc = DateTime.UtcNow;
            order.Error = null;
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
                order.Error = null;
            }
            else
            {
                order.Status = "Error";
                order.Error = exception.Message;
                logger.LogError(exception, "WebSocket-triggered order {OrderLinkId} failed", order.OrderLinkId);
                await telegram.OrderErrorAsync(order, token);
            }
        }
        await store.UpdateAsync(order, token);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }

    private static bool EntryReached(EntryWatch order, decimal price) => order.Direction switch
    {
        "Long" => price <= order.EntryPrice,
        "Short" => price >= order.EntryPrice,
        _ => false
    };

    private sealed record EntryWatch(Guid Id, string Symbol, string Direction, decimal EntryPrice);
}

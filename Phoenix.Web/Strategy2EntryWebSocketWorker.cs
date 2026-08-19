using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Phoenix.Web;

public sealed class Strategy2EntryWebSocketWorker(
    Strategy2Runtime runtime,
    Strategy2Worker engine,
    ILogger<Strategy2EntryWebSocketWorker> logger) : BackgroundService
{
    private static readonly Uri StreamUri = new("wss://stream.bybit.com/v5/public/linear");
    private readonly ConcurrentDictionary<Guid, Watch> _pending = new();

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!runtime.Options.Enabled) { await Task.Delay(1000, token); continue; }
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(StreamUri, token);
                var subscribed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                var subscriptions = MaintainAsync(socket, subscribed, linked.Token);
                await ReceiveAsync(socket, linked.Token);
                linked.Cancel();
                try { await subscriptions; } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Strategy 2 entry WebSocket disconnected; reconnecting");
                await Task.Delay(2000, token);
            }
        }
    }

    private async Task MaintainAsync(ClientWebSocket socket, HashSet<string> subscribed, CancellationToken token)
    {
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var active = (await runtime.Store.GetAllAsync(token)).Where(x => x.Status == "Pending").ToArray();
            var ids = active.Select(x => x.Id).ToHashSet();
            foreach (var stale in _pending.Keys.Where(x => !ids.Contains(x))) _pending.TryRemove(stale, out _);
            foreach (var x in active) _pending[x.Id] = new(x.Id, x.Symbol, x.Direction, x.EntryPrice);
            var additions = active.Select(x => x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(subscribed.Add).ToArray();
            if (additions.Length > 0)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    op = "subscribe", args = additions.Select(x => $"publicTrade.{x}").ToArray()
                });
                await socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, token);
            }
            await Task.Delay(1000, token);
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken token)
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
                if (symbol is not null && decimal.TryParse(trade.GetProperty("p").GetString(),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                    await HandleAsync(symbol, price, token);
            }
        }
    }

    private async Task HandleAsync(string symbol, decimal price, CancellationToken token)
    {
        foreach (var watch in _pending.Values.Where(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)))
        {
            var reached = watch.Direction == "Long" ? price <= watch.Entry : price >= watch.Entry;
            if (!reached) continue;
            var claim = await runtime.Store.TryClaimExclusiveSubmissionAsync(watch.Id, price, token);
            if (claim == ExclusiveClaimResult.Unavailable) continue;
            _pending.TryRemove(watch.Id, out _);
            var order = (await runtime.Store.GetAllAsync(token)).Single(x => x.Id == watch.Id);
            if (claim == ExclusiveClaimResult.Claimed) await engine.SubmitClaimedAsync(order, token);
            else await engine.ExpireBecauseBusyAsync(order, token);
        }
    }

    private sealed record Watch(Guid Id, string Symbol, string Direction, decimal Entry);
}

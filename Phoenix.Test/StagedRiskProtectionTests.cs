using System.Globalization;
using System.Net;
using System.Text.Json;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Web;

internal static class StagedRiskProtectionTests
{
    public static void BothDirections() => CheckBothDirections().GetAwaiter().GetResult();
    private static async Task CheckBothDirections()
    {
        foreach (var direction in new[] { "Long", "Short" })
        {
            using var h = new Harness(direction);
            await h.Save();
            Check(!await h.Advance(h.Level(.49m)) && h.Creates.Count == 0);
            Check(!await h.Advance(h.Level(.50m)) && h.Creates.Count == 1);
            CheckRequest(h.Creates[0], direction, "Limit", h.Level(.20m));
            Check(h.Signal.ProtectionStage == 1 && h.Signal.RiskFreeReachedAtUtc is null);
            h.Reload(); // Simulates process restart and verifies the store's clone and JSON fields.
            Check(h.Signal.EarlyStopLimitOrderId is not null && h.Signal.EarlyStopLimit == h.Level(.20m));
            await h.Advance(h.Level(.60m));
            Check(h.Creates.Count == 1);
            Check(await h.Advance(h.Level(.75m)) && h.Creates.Count == 3);
            CheckRequest(h.Creates[1], direction, "Limit", h.Level(.50m));
            CheckRequest(h.Creates[2], direction, "Market", h.Level(.20m));
            Check(h.Events.SequenceEqual(new[] { "Limit", "Limit", "Market", "Cancel" }));
            Check(h.Signal.RiskFreeReachedAtUtc is not null && h.Signal.EarlyStopLimitCancelledAtUtc is not null);
            h.Reload();
            await h.Advance(h.Level(.80m));
            Check(h.Creates.Count == 3 && h.Events.Count(x => x == "Cancel") == 1);
            h.Status[h.Signal.StopLoss2OrderId!] = "Filled";
            Check((await h.Protection.FilledAsync(h.Signal, default))?.OrderId == h.Signal.StopLoss2OrderId);
        }
    }

    public static void GapsAndFailures() => CheckGapsAndFailures().GetAwaiter().GetResult();
    private static async Task CheckGapsAndFailures()
    {
        using (var h = new Harness("Short"))
        {
            await h.Save();
            Check(await h.Advance(h.Level(.80m)));
            Check(h.Creates.Count == 2 && h.Signal.EarlyStopLimitOrderId is null);
            CheckRequest(h.Creates[0], "Short", "Limit", h.Level(.50m));
            CheckRequest(h.Creates[1], "Short", "Market", h.Level(.20m));
        }
        using (var h = new Harness("Long"))
        {
            await h.Save(); await h.Advance(h.Level(.50m));
            h.FailMarket = true;
            await ExpectFailure(() => h.Advance(h.Level(.75m)));
            Check(h.Signal.ProtectionStage == 2 && h.Signal.RiskFreeReachedAtUtc is null);
            Check(h.Signal.StopLoss2OrderId is not null && !h.Events.Contains("Cancel"));
            h.Reload(); h.FailMarket = false; h.ConfirmCancellation = false;
            // The 75% threshold remains latched even if price retreats before retry.
            await ExpectFailure(() => h.Advance(h.Level(.70m)));
            Check(h.Creates.Count == 3 && h.Signal.RiskFreeReachedAtUtc is null);
            Check(h.Signal.EarlyStopLimitCancelledAtUtc is null);
            h.Reload(); h.Status[h.Signal.EarlyStopLimitOrderId!] = "Cancelled";
            Check(await h.Advance(h.Level(.70m)));
            Check(h.Creates.Count == 3 && h.Signal.EarlyStopLimitCancelledAtUtc is not null);
        }
    }

    public static void DuplicateAndEarlyFill() => CheckDuplicateAndEarlyFill().GetAwaiter().GetResult();
    private static async Task CheckDuplicateAndEarlyFill()
    {
        using var h = new Harness("Long");
        await h.Save(); await h.Advance(h.Level(.50m));
        var early = h.Signal.EarlyStopLimitOrderId!;
        h.Signal.EarlyStopLimitOrderId = null; await h.Save(); h.Reload();
        // Exchange accepted the first request but the process lost its persisted acknowledgement.
        await h.Advance(h.Level(.55m));
        Check(h.Creates.Count == 1 && h.Signal.EarlyStopLimitOrderId == early);
        h.Status[early] = "Filled";
        Check((await h.Protection.FilledAsync(h.Signal, default))?.OrderId == early);
        Check(StagedRiskProtection.OrderIds(h.Signal).Single() == early);
    }

    private static async Task ExpectFailure(Func<Task<bool>> action)
    {
        try { await action(); }
        catch (Exception) { return; }
        throw new Exception("Expected an exchange or cancellation failure.");
    }
    private static void Check(bool value) { if (!value) throw new Exception("Staged protection assertion failed."); }
    private static void CheckRequest(JsonElement body, string direction, string type, decimal level)
    {
        Check(body.GetProperty("orderType").GetString() == type);
        Check(body.GetProperty("side").GetString() == (direction == "Long" ? "Sell" : "Buy"));
        Check(body.GetProperty("triggerDirection").GetInt32() == (direction == "Long" ? 2 : 1));
        Check(body.GetProperty("triggerPrice").GetString() == level.ToString("0.################", CultureInfo.InvariantCulture));
        Check(body.GetProperty("qty").GetString() == "0.01");
        Check(body.GetProperty("reduceOnly").GetBoolean() && body.GetProperty("closeOnTrigger").GetBoolean());
        Check(body.GetProperty("triggerBy").GetString() == "MarkPrice");
        if (type == "Limit") Check(body.GetProperty("price").GetString() == body.GetProperty("triggerPrice").GetString());
        else Check(!body.TryGetProperty("price", out _));
    }

    private sealed class Harness : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "phoenix-stages-" + Guid.NewGuid());
        private ServerOrderStore store;
        private readonly BybitDemoClient client;
        public ServerSignal Signal;
        public List<JsonElement> Creates = [];
        public List<string> Events = [];
        public Dictionary<string, string> Status = [];
        private readonly Dictionary<string, string> links = [];
        public bool FailMarket;
        public bool ConfirmCancellation = true;
        public StagedRiskProtection Protection => new(client, store);
        public Harness(string direction)
        {
            Directory.CreateDirectory(directory);
            store = MakeStore();
            Signal = new() { Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = direction, Status = "Filled",
                EntryPrice = 100, TakeProfit = direction == "Long" ? 120 : 80, StopLoss = direction == "Long" ? 90 : 110,
                Quantity = .02m, ExecutedQuantity = .01m, CreatedAtUtc = DateTime.UtcNow };
            client = new(new BybitDemoOptions("key", "secret"), new HttpClient(new Handler(Respond)));
        }
        private ServerOrderStore MakeStore() => new(Path.Combine(directory, "queue.json"), Path.Combine(directory, "history.db"));
        public decimal Level(decimal fraction) => StagedRiskProtection.Level(Signal, fraction);
        public async Task Save()
        {
            if ((await store.GetAllAsync()).Count == 0) await store.AddAsync(Signal);
            else await store.UpdateAsync(Signal);
        }
        public void Reload() { store = MakeStore(); Signal = store.GetAllAsync().GetAwaiter().GetResult().Single(); }
        public Task<bool> Advance(decimal price) => Protection.AdvanceAsync(Signal, price, default);
        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("instruments-info"))
                return Json("{\"list\":[{\"symbol\":\"BTCUSDT\",\"priceFilter\":{\"tickSize\":\"0.1\"},\"lotSizeFilter\":{\"qtyStep\":\"0.001\",\"minOrderQty\":\"0.001\",\"minNotionalValue\":\"5\"},\"leverageFilter\":{\"maxLeverage\":\"100\",\"minLeverage\":\"1\",\"leverageStep\":\"0.01\"}}]}");
            if (path.EndsWith("position/list")) return Json("{\"list\":[]}");
            if (path.EndsWith("order/realtime"))
            {
                var query = request.RequestUri.Query;
                var id = Status.Keys.FirstOrDefault(x => query.Contains("orderId=" + x, StringComparison.Ordinal));
                if (id is null)
                {
                    var link = links.Keys.FirstOrDefault(x => query.Contains("orderLinkId=" + x, StringComparison.Ordinal));
                    if (link is not null) id = links[link];
                }
                if (id is null) return Json("{\"list\":[]}");
                return Json(JsonSerializer.Serialize(new { list = new[] { new { orderId = id, symbol = "BTCUSDT",
                    orderStatus = Status[id], avgPrice = "100", cumExecQty = Status[id] == "Filled" ? "0.01" : "0" } } }));
            }
            using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var body = doc.RootElement;
            if (path.EndsWith("order/cancel"))
            {
                var id = body.GetProperty("orderId").GetString()!; Events.Add("Cancel");
                if (ConfirmCancellation) Status[id] = "Cancelled";
                return Json(JsonSerializer.Serialize(new { orderId = id, orderLinkId = "" }));
            }
            Check(path.EndsWith("order/create"));
            var type = body.GetProperty("orderType").GetString()!;
            if (type == "Market" && FailMarket) return new(HttpStatusCode.ServiceUnavailable);
            var key = body.GetProperty("orderLinkId").GetString()!;
            if (links.ContainsKey(key)) return new(HttpStatusCode.OK)
                { Content = new StringContent("{\"retCode\":110072,\"retMsg\":\"duplicate\",\"result\":{}}") };
            Creates.Add(body.Clone()); Events.Add(type);
            var newId = "order-" + Creates.Count; links[key] = newId; Status[newId] = "Untriggered";
            return Json(JsonSerializer.Serialize(new { orderId = newId, orderLinkId = key }));
        }
        private static HttpResponseMessage Json(string result) => new(HttpStatusCode.OK)
            { Content = new StringContent("{\"retCode\":0,\"result\":" + result + "}") };
        public void Dispose() { SignalHistoryStore.ClearConnectionPools(); Directory.Delete(directory, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(send(request));
    }
}

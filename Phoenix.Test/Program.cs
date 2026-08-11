using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;

var passed = 0;
var failed = 0;

Run("Long strategy calculates expected levels", () =>
{
    var signal = BuildSignal(Direction.Long);
    var plan = new StrategyCalculator().Calculate(signal);
    Equal(118764m, plan.EntryPrice);
    Equal(119000m, plan.TakeProfit);
    Equal(118542m, plan.StopLoss1);
});

Run("Short strategy calculates expected levels", () =>
{
    var signal = BuildSignal(Direction.Short);
    var plan = new StrategyCalculator().Calculate(signal);
    Equal(119236m, plan.EntryPrice);
    Equal(119000m, plan.TakeProfit);
    Equal(119458m, plan.StopLoss1);
});

Run("Invalid range is rejected", () =>
{
    var signal = BuildSignal(Direction.Long);
    signal.High = signal.Low;
    Throws<ArgumentException>(() => new StrategyCalculator().Calculate(signal));
});

Run("Position quantity is calculated from USDT value", () =>
{
    var signal = Prepare(BuildSignal(Direction.Long));
    var position = new ExecutionManager().OpenPosition(signal)!;
    Equal(signal.PositionSizeUsdt / signal.TradePlan!.EntryPrice, position.Quantity);
    Equal(100m, position.PositionSizeUsdt);
});

Run("Paper exchange records full protective order set", () =>
{
    var signal = Prepare(BuildSignal(Direction.Long));
    var position = new ExecutionManager().OpenPosition(signal)!;
    var exchange = new PaperExchange();
    True(new OrderManager(exchange).PlaceOrders(position));
    Equal(4, exchange.Orders.Count);
    Equal("FILLED", exchange.Orders[0].Status);
    Equal("WAITING", exchange.Orders[3].Status);
});

Run("Entry rules respect trade direction", () =>
{
    var longSignal = Prepare(BuildSignal(Direction.Long));
    True(new EntryManager().CanOpenPosition(longSignal, longSignal.TradePlan!.EntryPrice));
    False(new EntryManager().CanOpenPosition(longSignal, longSignal.TradePlan.EntryPrice + 1));

    var shortSignal = Prepare(BuildSignal(Direction.Short));
    True(new EntryManager().CanOpenPosition(shortSignal, shortSignal.TradePlan!.EntryPrice));
    False(new EntryManager().CanOpenPosition(shortSignal, shortSignal.TradePlan.EntryPrice - 1));
});

Run("Bybit HMAC uses lowercase SHA-256 signature", () =>
{
    var signature = BybitSignature.CreateHmacSha256(
        "key",
        "The quick brown fox jumps over the lazy dog");
    Equal("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", signature);
});

Run("Bybit signed requests contain required V5 headers", () =>
{
    var options = new BybitDemoOptions("test-key", "test-secret");
    var client = new BybitDemoClient(options, timestampProvider: () => 1_700_000_000_000);
    using var request = client.CreateSignedGetRequest("/v5/account/wallet-balance", "accountType=UNIFIED&coin=USDT");
    Equal("test-key", request.Headers.GetValues("X-BAPI-API-KEY").Single());
    Equal("1700000000000", request.Headers.GetValues("X-BAPI-TIMESTAMP").Single());
    Equal("5000", request.Headers.GetValues("X-BAPI-RECV-WINDOW").Single());
    Equal(64, request.Headers.GetValues("X-BAPI-SIGN").Single().Length);
});

Run("Bybit private requests require Demo credentials", () =>
{
    var client = new BybitDemoClient(new BybitDemoOptions(null, null));
    Throws<InvalidOperationException>(() =>
        client.CreateSignedGetRequest("/v5/account/wallet-balance", "accountType=UNIFIED"));
});

Run("Bybit order preview follows instrument precision", () =>
{
    var signal = BuildSignal(Direction.Long);
    signal.PositionSizeUsdt = 200m;
    Prepare(signal);
    var position = new ExecutionManager().OpenPosition(signal)!;
    var rules = new BybitInstrumentRules("BTCUSDT", 0.10m, 0.001m, 0.001m, 5m);
    var preview = BybitOrderPreviewBuilder.Build("btcusdt", position, rules);
    Equal(0.001m, preview.Quantity);
    Equal(118764.00m, preview.Price);
    Equal(119000.00m, preview.TakeProfit);
    Equal(118542.00m, preview.StopLoss);
    Equal("Buy", preview.Side);
});

Run("Bybit order preview rejects orders below minimum", () =>
{
    var signal = Prepare(BuildSignal(Direction.Long));
    signal.PositionSizeUsdt = 1m;
    var position = new ExecutionManager().OpenPosition(signal)!;
    var rules = new BybitInstrumentRules("BTCUSDT", 0.10m, 0.001m, 0.001m, 5m);
    Throws<InvalidOperationException>(() => BybitOrderPreviewBuilder.Build("BTCUSDT", position, rules));
});

Run("Bybit POST requests sign the exact JSON body", () =>
{
    const string body = "{\"category\":\"linear\",\"symbol\":\"BTCUSDT\"}";
    var options = new BybitDemoOptions("test-key", "test-secret");
    var client = new BybitDemoClient(options, timestampProvider: () => 1_700_000_000_000);
    using var request = client.CreateSignedPostRequest("/v5/order/create", body);
    Equal(HttpMethod.Post, request.Method);
    Equal("application/json", request.Content!.Headers.ContentType!.MediaType!);
    Equal(body, request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    Equal(64, request.Headers.GetValues("X-BAPI-SIGN").Single().Length);
});

Run("Bybit Demo limit orders include protection and return an order ID", () =>
{
    var handler = new StubHttpHandler(request =>
    {
        Equal("/v5/order/create", request.RequestUri!.AbsolutePath);
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        True(body.Contains("\"orderType\":\"Limit\"", StringComparison.Ordinal));
        True(body.Contains("\"takeProfit\":\"119000\"", StringComparison.Ordinal));
        True(body.Contains("\"stopLoss\":\"118542\"", StringComparison.Ordinal));
        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"orderId\":\"demo-123\",\"orderLinkId\":\"phoenix-test\"}}")
        };
    });
    var client = new BybitDemoClient(
        new BybitDemoOptions("test-key", "test-secret"),
        new HttpClient(handler),
        () => 1_700_000_000_000);
    var preview = new BybitOrderPreview("BTCUSDT", "Buy", 0.001m, 118764m, 119000m, 118542m, 118.764m);
    var result = client.PlaceLimitOrderAsync(preview).GetAwaiter().GetResult();
    Equal("demo-123", result.OrderId);
});

Console.WriteLine($"\nResult: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS  {name}"); }
    catch (Exception exception) { failed++; Console.WriteLine($"FAIL  {name}: {exception.Message}"); }
}

static Signal BuildSignal(Direction direction) => new()
{
    Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = direction,
    High = 120000m, Low = 118000m, PositionSizeUsdt = 100m,
    Status = SignalStatus.WaitingEntry, CreatedAt = DateTime.UtcNow
};

static Signal Prepare(Signal signal)
{
    new SignalManager(new CalculationService(new StrategyCalculator())).AddSignal(signal);
    return signal;
}

static void True(bool value) { if (!value) throw new Exception("Expected true."); }
static void False(bool value) { if (value) throw new Exception("Expected false."); }
static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
}

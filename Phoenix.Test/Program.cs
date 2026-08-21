using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;
using Phoenix.Web;

var passed = 0;
var failed = 0;

Run("Long strategy calculates expected levels", () =>
{
    var signal = BuildSignal(Direction.Long);
    var plan = new StrategyCalculator().Calculate(signal);
    Near(118760.03488777m, plan.EntryPrice);
    Near(118995.798245148m, plan.TakeProfit);
    Near(118538.683877804m, plan.StopLoss1);
    Near(118818.975727114m, plan.StopLoss2!.Value);
    Near(118936.857405803m, plan.RiskFreePrice);
});

Run("Short strategy calculates expected levels", () =>
{
    var signal = BuildSignal(Direction.Short);
    var plan = new StrategyCalculator().Calculate(signal);
    Near(119232.029641802m, plan.EntryPrice);
    Near(118995.798245148m, plan.TakeProfit);
    Near(119454.675358104m, plan.StopLoss1);
    Near(119172.971792639m, plan.StopLoss2!.Value);
    Near(119054.856094312m, plan.RiskFreePrice);
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
    Equal(signal.PositionSizeUsdt * signal.TradePlan!.Leverage / signal.TradePlan.EntryPrice, position.Quantity);
    Equal(100m, position.PositionSizeUsdt);
});

Run("Leverage targets fifty percent return at take profit", () =>
{
    var plan = new StrategyCalculator().Calculate(BuildSignal(Direction.Long));
    var targetMovePercent = Math.Abs(plan.EntryPrice - plan.TakeProfit) / plan.EntryPrice * 100m;
    Equal(50m, plan.Leverage * targetMovePercent);
});

Run("Strategy 2 leverage targets twenty percent return at take profit", () =>
{
    var plan = new StrategyCalculator().Calculate(BuildSignal(Direction.Long));
    var leverage = StrategyCalculator.CalculateLeverage(
        plan.EntryPrice, plan.TakeProfit, 20m);
    var targetMovePercent = Math.Abs(plan.EntryPrice - plan.TakeProfit) / plan.EntryPrice * 100m;
    Near(20m, leverage * targetMovePercent);
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
    Equal(0.424m, preview.Quantity);
    Equal(118760.00m, preview.Price);
    Equal(118995.80m, preview.TakeProfit);
    Equal(118538.70m, preview.StopLoss);
    Equal("Buy", preview.Side);
});

Run("Bybit order preview rejects orders below minimum", () =>
{
    var signal = Prepare(BuildSignal(Direction.Long));
    signal.PositionSizeUsdt = 0.01m;
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
    var result = client.PlaceLimitOrderAsync(preview, "phoenix-test").GetAwaiter().GetResult();
    Equal("demo-123", result.OrderId);
});

Run("Bybit leverage is read from the Demo position setting", () =>
{
    var handler = new StubHttpHandler(request =>
    {
        Equal("/v5/position/list", request.RequestUri!.AbsolutePath);
        Equal("category=linear&symbol=BTCUSDT", request.RequestUri.Query.TrimStart('?'));
        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"list\":[{\"symbol\":\"BTCUSDT\",\"leverage\":\"12\"}]}}")
        };
    });
    var client = new BybitDemoClient(
        new BybitDemoOptions("test-key", "test-secret"),
        new HttpClient(handler),
        () => 1_700_000_000_000);
    Equal(12m, client.GetLeverageAsync("btcusdt").GetAwaiter().GetResult()!.Value);
});

Run("Bybit position index follows account position mode", () =>
{
    var handler = new StubHttpHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"list\":[{\"positionIdx\":1},{\"positionIdx\":2}]}}")
    });
    var client = new BybitDemoClient(
        new BybitDemoOptions("test-key", "test-secret"), new HttpClient(handler));
    Equal(1, client.GetPositionIndexAsync("BTCUSDT", "Buy").GetAwaiter().GetResult());
    Equal(2, client.GetPositionIndexAsync("BTCUSDT", "Sell").GetAwaiter().GetResult());
});

Run("Bybit instrument rules expose maximum leverage", () =>
{
    var handler = new StubHttpHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"list\":[{\"symbol\":\"BTCUSDT\",\"priceFilter\":{\"tickSize\":\"0.1\"},\"lotSizeFilter\":{\"qtyStep\":\"0.001\",\"minOrderQty\":\"0.001\",\"minNotionalValue\":\"5\"},\"leverageFilter\":{\"maxLeverage\":\"100\",\"minLeverage\":\"1\",\"leverageStep\":\"0.01\"}}]}}")
    });
    var client = new BybitDemoClient(new BybitDemoOptions(null, null), new HttpClient(handler));
    var rules = client.GetInstrumentRulesAsync("BTCUSDT").GetAwaiter().GetResult();
    Equal(100m, rules.MaximumLeverage);
});

Run("Calculated leverage is capped and rounded down to Bybit rules", () =>
{
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    Equal(100m, BybitLeverageRules.Normalize(120m, rules));
    Equal(12.34m, BybitLeverageRules.Normalize(12.349m, rules));
});

Run("Phoenix sets both Bybit leverage sides before entry", () =>
{
    var handler = new StubHttpHandler(request =>
    {
        Equal("/v5/position/set-leverage", request.RequestUri!.AbsolutePath);
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        True(body.Contains("\"buyLeverage\":\"12.34\"", StringComparison.Ordinal));
        True(body.Contains("\"sellLeverage\":\"12.34\"", StringComparison.Ordinal));
        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{}}")
        };
    });
    var client = new BybitDemoClient(new BybitDemoOptions("key", "secret"), new HttpClient(handler));
    client.SetLeverageAsync("BTCUSDT", 12.34m).GetAwaiter().GetResult();
});

Run("Queued order entry rules respect Buy and Sell direction", () =>
{
    var buy = new QueuedOrder { Side = "Buy", EntryPrice = 100m };
    True(QueuedOrderRules.IsEntryReached(buy, 99m));
    False(QueuedOrderRules.IsEntryReached(buy, 101m));

    var sell = new QueuedOrder { Side = "Sell", EntryPrice = 100m };
    True(QueuedOrderRules.IsEntryReached(sell, 101m));
    False(QueuedOrderRules.IsEntryReached(sell, 99m));
});

Run("Order queue persists across application restarts", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"phoenix-queue-{Guid.NewGuid():N}.json");
    try
    {
        var store = new OrderQueueStore(path);
        store.Save([new QueuedOrder
        {
            Symbol = "BTCUSDT", Side = "Buy", Quantity = 0.001m,
            EntryPrice = 100m, TakeProfit = 110m, StopLoss = 90m
        }]);
        var restored = store.Load();
        Equal(1, restored.Count);
        Equal("BTCUSDT", restored[0].Symbol);
        Equal(QueuedOrderStatus.Pending, restored[0].Status);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }
});

Run("Bybit SL2 is a reduce-only conditional stop-limit order", () =>
{
    var requests = new List<string>();
    var handler = new StubHttpHandler(request =>
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        requests.Add(body);
        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"orderId\":\"sl2-123\",\"orderLinkId\":\"sl2-test\"}}")
        };
    });
    var client = new BybitDemoClient(new BybitDemoOptions("key", "secret"), new HttpClient(handler));
    client.PlaceStopLimitAsync("BTCUSDT", "Long", 0.01m, 101.25m, 0.1m, "sl2-test").GetAwaiter().GetResult();
    True(requests[0].Contains("\"side\":\"Sell\"", StringComparison.Ordinal));
    True(requests[0].Contains("\"orderType\":\"Limit\"", StringComparison.Ordinal));
    True(requests[0].Contains("\"triggerDirection\":2", StringComparison.Ordinal));
    True(requests[0].Contains("\"triggerPrice\":\"101.3\"", StringComparison.Ordinal));
    True(requests[0].Contains("\"price\":\"101.3\"", StringComparison.Ordinal));
    True(requests[0].Contains("\"reduceOnly\":true", StringComparison.Ordinal));
    True(requests[0].Contains("\"closeOnTrigger\":true", StringComparison.Ordinal));
    client.PlaceStopLimitAsync("BTCUSDT", "Short", 0.01m, 98.75m, 0.1m, "sl2-short").GetAwaiter().GetResult();
    True(requests[1].Contains("\"side\":\"Buy\"", StringComparison.Ordinal));
    True(requests[1].Contains("\"triggerDirection\":1", StringComparison.Ordinal));
});

Run("Server signal queue persists across application restarts", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"phoenix-server-queue-{Guid.NewGuid():N}.json");
    var historyPath = Path.ChangeExtension(path, ".db");
    var previous = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");
    var previousHistory = Environment.GetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH");
    try
    {
        Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH", path);
        Environment.SetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH", historyPath);
        var signal = new ServerSignal
        {
            Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = "Long", Quantity = 0.001m,
            EntryPrice = 100m, TakeProfit = 110m, StopLoss = 90m, Leverage = 12m,
            OrderLinkId = "phoenix-server-test", CreatedAtUtc = DateTime.UtcNow
        };
        new ServerOrderStore().AddAsync(signal).GetAwaiter().GetResult();
        var restored = new ServerOrderStore().GetAllAsync().GetAwaiter().GetResult();
        Equal(1, restored.Count);
        Equal("phoenix-server-test", restored[0].OrderLinkId);
        Equal("Pending", restored[0].Status);
        Equal(12m, restored[0].Leverage!.Value);
        True(new ServerOrderStore().RemoveAsync(signal.Id).GetAwaiter().GetResult());
        Equal(0, new ServerOrderStore().GetAllAsync().GetAwaiter().GetResult().Count);
        var history = new ServerOrderStore().GetHistoryAsync().GetAwaiter().GetResult();
        Equal(1, history.Count);
        Equal(signal.Id, history[0].Signal.Id);
        True(history[0].RemovedAtUtc is not null);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH", previous);
        Environment.SetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH", previousHistory);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        if (File.Exists(historyPath)) File.Delete(historyPath);
        if (File.Exists(historyPath + "-wal")) File.Delete(historyPath + "-wal");
        if (File.Exists(historyPath + "-shm")) File.Delete(historyPath + "-shm");
    }
});

Run("Server lifecycle levels respect Long direction", () =>
{
    var signal = new ServerSignal { Direction = "Long", TakeProfit = 110m, StopLoss = 90m };
    True(DemoOrderWorker.TargetReached(signal, 110m));
    False(DemoOrderWorker.TargetReached(signal, 109m));
    True(DemoOrderWorker.StopLossReached(signal, 90m));
    False(DemoOrderWorker.StopLossReached(signal, 91m));
});

Run("Server lifecycle levels respect Short direction", () =>
{
    var signal = new ServerSignal { Direction = "Short", TakeProfit = 90m, StopLoss = 110m };
    True(DemoOrderWorker.TargetReached(signal, 90m));
    False(DemoOrderWorker.TargetReached(signal, 91m));
    True(DemoOrderWorker.StopLossReached(signal, 110m));
    False(DemoOrderWorker.StopLossReached(signal, 109m));
});

Run("Only one trigger can claim a pending server signal", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"phoenix-claim-{Guid.NewGuid():N}.json");
    var historyPath = Path.ChangeExtension(path, ".db");
    var previous = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");
    var previousHistory = Environment.GetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH");
    try
    {
        Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH", path);
        Environment.SetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH", historyPath);
        var store = new ServerOrderStore();
        var signal = new ServerSignal { Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = "Long",
            EntryPrice = 100m, Status = "Pending", CreatedAtUtc = DateTime.UtcNow };
        store.AddAsync(signal).GetAwaiter().GetResult();
        var claims = Task.WhenAll(
            store.TryClaimSubmissionAsync(signal.Id, 100m),
            store.TryClaimSubmissionAsync(signal.Id, 99.9m)).GetAwaiter().GetResult();
        Equal(1, claims.Count(x => x));
    }
    finally
    {
        Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH", previous);
        Environment.SetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH", previousHistory);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + ".tmp", historyPath, historyPath + "-wal", historyPath + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }
});

Run("Stale pending snapshot cannot undo an entry claim", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"phoenix-stale-claim-{Guid.NewGuid():N}.json");
    var historyPath = Path.ChangeExtension(path, ".db");
    var previous = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");
    var previousHistory = Environment.GetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH");
    try
    {
        Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH", path);
        Environment.SetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH", historyPath);
        var store = new ServerOrderStore();
        var signal = new ServerSignal { Id = Guid.NewGuid(), Symbol = "LINKUSDT", Direction = "Short",
            EntryPrice = 10m, Status = "Pending", CreatedAtUtc = DateTime.UtcNow };
        store.AddAsync(signal).GetAwaiter().GetResult();
        var stale = store.GetAllAsync().GetAwaiter().GetResult().Single();
        True(store.TryClaimSubmissionAsync(signal.Id, 10m).GetAwaiter().GetResult());
        store.UpdateAsync(stale).GetAwaiter().GetResult();
        Equal("Submitting", store.GetAllAsync().GetAwaiter().GetResult().Single().Status);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH", previous);
        Environment.SetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH", previousHistory);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + ".tmp", historyPath, historyPath + "-wal", historyPath + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }
});

Run("Strategy 2 allows only one simultaneous entry claim", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"phoenix-exclusive-{Guid.NewGuid():N}.json");
    var historyPath = Path.ChangeExtension(path, ".db");
    try
    {
        var store = new ServerOrderStore(path, historyPath);
        var first = new ServerSignal { Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = "Long",
            EntryPrice = 100m, Status = "Pending", CreatedAtUtc = DateTime.UtcNow };
        var second = new ServerSignal { Id = Guid.NewGuid(), Symbol = "ETHUSDT", Direction = "Short",
            EntryPrice = 100m, Status = "Pending", CreatedAtUtc = DateTime.UtcNow };
        store.AddAsync(first).GetAwaiter().GetResult();
        store.AddAsync(second).GetAwaiter().GetResult();
        Equal(ExclusiveClaimResult.Claimed,
            store.TryClaimExclusiveSubmissionAsync(first.Id, 100m).GetAwaiter().GetResult());
        Equal(ExclusiveClaimResult.PositionBusy,
            store.TryClaimExclusiveSubmissionAsync(second.Id, 100m).GetAwaiter().GetResult());
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + ".tmp", historyPath, historyPath + "-wal", historyPath + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }
});

Run("Long expiry activates after twenty-five percent approach", () =>
{
    var signal = new ServerSignal
    {
        Direction = "Long", EntryPrice = 100m, TakeProfit = 110m,
        ExpireActivationPrice = 102.5m
    };
    False(DemoOrderWorker.ExpireActivationReached(signal, 102.51m));
    True(DemoOrderWorker.ExpireActivationReached(signal, 102.5m));
    False(DemoOrderWorker.TargetExpiryReached(signal, 109.99m));
    True(DemoOrderWorker.TargetExpiryReached(signal, 110m));
});

Run("Short expiry activates after twenty-five percent approach", () =>
{
    var signal = new ServerSignal
    {
        Direction = "Short", EntryPrice = 110m, TakeProfit = 100m,
        ExpireActivationPrice = 107.5m
    };
    False(DemoOrderWorker.ExpireActivationReached(signal, 107.49m));
    True(DemoOrderWorker.ExpireActivationReached(signal, 107.5m));
    False(DemoOrderWorker.TargetExpiryReached(signal, 100.01m));
    True(DemoOrderWorker.TargetExpiryReached(signal, 100m));
});

Run("First terminal result is normalized and locked", () =>
{
    var targetAt = DateTime.UtcNow.AddMinutes(-10);
    var signal = new ServerSignal
    {
        Status = "Filled", TargetReachedAtUtc = targetAt,
        StopLossReachedAtUtc = targetAt.AddMinutes(5)
    };
    True(ServerOrderStore.NormalizeTerminalState(signal));
    Equal("Target", signal.Outcome!);
    Equal("Completed", signal.Status);
    Equal(targetAt, signal.CompletedAtUtc!.Value);
    True(signal.StopLossReachedAtUtc is null);
    False(ServerOrderStore.NormalizeTerminalState(signal));
});

Run("Expired result preserves its reason", () =>
{
    var signal = new ServerSignal
    {
        Status = "Expired", ExpireStage = "Target", ExpiredAtUtc = DateTime.UtcNow
    };
    True(ServerOrderStore.NormalizeTerminalState(signal));
    Equal("Expired", signal.Outcome!);
    Equal("TargetAfterActivation", signal.ExpireReason!);
});

Run("Bybit candles are parsed and sorted oldest first", () =>
{
    var handler = new StubHttpHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"list\":[[\"2000\",\"20\",\"24\",\"19\",\"23\",\"10\",\"0\"],[\"1000\",\"10\",\"21\",\"9\",\"20\",\"12\",\"0\"]]}}")
    });
    var client = new BybitDemoClient(new BybitDemoOptions(null, null), new HttpClient(handler));
    var candles = client.GetKlinesAsync("btcusdt", "60", 50).GetAwaiter().GetResult();
    Equal(2, candles.Count);
    Equal(1000L, candles[0].OpenTime);
    Equal(23m, candles[1].Close);
});

Run("Elliott analyzer returns a valid bullish impulse", () =>
{
    var prices = Enumerable.Range(0, 100).Select(i => 100m + i * 0.01m).ToArray();
    void Shape(int center, decimal price, bool high)
    {
        for (var offset = -3; offset <= 3; offset++)
        {
            var distance = Math.Abs(offset);
            prices[center + offset] = high ? price - distance : price + distance;
        }
    }
    Shape(10, 100m, false); Shape(25, 120m, true); Shape(38, 110m, false);
    Shape(52, 145m, true); Shape(67, 130m, false); Shape(82, 155m, true);
    var candles = prices.Select((price, index) => new BybitKline(index * 60_000L, price, price + 0.1m, price - 0.1m, price, 1m)).ToArray();
    var analysis = new ElliottWaveAnalyzer().Analyze(candles, 3, 2m);
    True(analysis.Scenarios.Count > 0);
    Equal("Bullish", analysis.Scenarios[0].Direction);
    True(analysis.Scenarios[0].Rules.Single(x => x.Code == "wave3").Passed);
});

Run("Signal Lab candidate uses confirmed range and Phoenix calculations", () =>
{
    var prices = Enumerable.Range(0, 100).Select(i => 100m + i * 0.08m).ToArray();
    for (var offset = -3; offset <= 3; offset++)
    {
        prices[30 + offset] = 120m - Math.Abs(offset);
        prices[58 + offset] = 103m + Math.Abs(offset);
        prices[78 + offset] = 126m - Math.Abs(offset);
    }
    var candles = prices.Select((price, index) => new BybitKline(index * 60_000L,
        price, price + 0.2m, price - 0.2m, price, 10m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var candidate = new SignalCandidateFinder(new StrategyCalculator())
        .Find("BTCUSDT", "60", candles, rules, 25m, 3);
    True(candidate.Ceiling > candidate.Floor);
    Equal("Long", candidate.Direction);
    True(candidate.EntryPrice > candidate.Floor && candidate.EntryPrice < candidate.Ceiling);
    True(candidate.Leverage >= 1m && candidate.Leverage <= 100m);
    True(candidate.Confidence is >= 35m and <= 92m);
    var lineCandidate = new SignalCandidateFinder(new StrategyCalculator())
        .Find("BTCUSDT", "60", candles, rules, 25m, 3, useClosePrices: true);
    Equal(126m, lineCandidate.Ceiling);
});

Run("Signal Lab moves Long floor after an intermediate 61.8 percent entry was touched", () =>
{
    var prices = Enumerable.Range(0, 120).Select(_ => 100m).ToArray();
    void Pivot(int center, decimal price, bool high)
    {
        for (var offset = -3; offset <= 3; offset++)
            prices[center + offset] = high ? price - Math.Abs(offset) : price + Math.Abs(offset);
    }
    Pivot(18, 80m, false);
    Pivot(38, 120m, true);
    Pivot(55, 90m, false); // below the logarithmic 61.8% entry of the first range
    Pivot(82, 150m, true);
    var candles = prices.Select((price, index) => new BybitKline(index * 60_000L,
        price, price + 0.1m, price - 0.1m, price, 1m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var candidate = new SignalCandidateFinder(new StrategyCalculator())
        .Find("BTCUSDT", "60", candles, rules, 25m, 3, useClosePrices: true);
    Equal("Long", candidate.Direction);
    Equal(90m, candidate.Floor);
    Equal(150m, candidate.Ceiling);
    True(candidate.IsBurned);
});

Run("Signal Lab keeps a candidate active while entry has not been touched", () =>
{
    var prices = Enumerable.Range(0, 100).Select(i => i < 60 ? 120m : 145m).ToArray();
    for (var offset = -3; offset <= 3; offset++)
    {
        prices[20 + offset] = 80m + Math.Abs(offset);
        prices[60 + offset] = 150m - Math.Abs(offset);
    }
    var candles = prices.Select((price, index) => new BybitKline(index * 60_000L,
        price, price + 0.1m, price - 0.1m, price, 1m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var candidate = new SignalCandidateFinder(new StrategyCalculator())
        .Find("BTCUSDT", "60", candles, rules, 25m, 3, useClosePrices: true);
    Equal("Long", candidate.Direction);
    False(candidate.IsBurned);
    True(candidate.EntryTouchedTime is null);
});

Run("Signal Lab direction is Short when major high comes before major low", () =>
{
    var prices = Enumerable.Range(0, 100).Select(_ => 100m).ToArray();
    for (var offset = -3; offset <= 3; offset++)
    {
        prices[25 + offset] = 140m - Math.Abs(offset);
        prices[70 + offset] = 60m + Math.Abs(offset);
    }
    var candles = prices.Select((price, index) => new BybitKline(index * 60_000L,
        price, price + 0.1m, price - 0.1m, price, 1m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var candidate = new SignalCandidateFinder(new StrategyCalculator())
        .Find("BTCUSDT", "60", candles, rules, 25m, 3);
    Equal("Short", candidate.Direction);
    True(candidate.CeilingTime < candidate.FloorTime);
});

Run("Signal Lab Short floor uses the lowest shadow or close in the visible range", () =>
{
    var prices = Enumerable.Range(0, 100).Select(_ => 100m).ToArray();
    for (var offset = -3; offset <= 3; offset++)
    {
        prices[20 + offset] = 140m - Math.Abs(offset);
        prices[60 + offset] = 70m + Math.Abs(offset);
    }
    prices[68] = 75m;
    var candles = prices.Select((price, index) => new BybitKline(index * 60_000L,
        price, price + 0.1m, index == 68 ? 50m : price - 0.1m, price, 1m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var finder = new SignalCandidateFinder(new StrategyCalculator());
    var candleCandidate = finder.Find("BTCUSDT", "60", candles, rules, 25m, 3);
    var lineCandidate = finder.Find("BTCUSDT", "60", candles, rules, 25m, 3, useClosePrices: true);
    Equal("Short", candleCandidate.Direction);
    Equal(50m, candleCandidate.Floor);
    Equal(70m, lineCandidate.Floor);
});

Run("Signal Lab Long ceiling uses the highest shadow or close in the visible range", () =>
{
    var prices = Enumerable.Range(0, 100).Select(_ => 100m).ToArray();
    for (var offset = -3; offset <= 3; offset++)
    {
        prices[20 + offset] = 60m + Math.Abs(offset);
        prices[60 + offset] = 130m - Math.Abs(offset);
    }
    prices[68] = 125m;
    var candles = prices.Select((price, index) => new BybitKline(index * 60_000L,
        price, index == 68 ? 160m : price + 0.1m, price - 0.1m, price, 1m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var finder = new SignalCandidateFinder(new StrategyCalculator());
    var candleCandidate = finder.Find("BTCUSDT", "60", candles, rules, 25m, 3);
    var lineCandidate = finder.Find("BTCUSDT", "60", candles, rules, 25m, 3, useClosePrices: true);
    Equal("Long", candleCandidate.Direction);
    Equal(160m, candleCandidate.Ceiling);
    Equal(130m, lineCandidate.Ceiling);
});

Run("Signal Lab applies the absolute extreme only to the second anchor", () =>
{
    var longPrices = Enumerable.Range(0, 100).Select(_ => 100m).ToArray();
    for (var offset = -3; offset <= 3; offset++)
    {
        longPrices[20 + offset] = 60m + Math.Abs(offset);
        longPrices[60 + offset] = 130m - Math.Abs(offset);
    }
    var longCandles = longPrices.Select((price, index) => new BybitKline(index * 60_000L,
        price, index == 68 ? 160m : price + 0.1m, index == 1 ? 30m : price - 0.1m, price, 1m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var finder = new SignalCandidateFinder(new StrategyCalculator());
    var longCandidate = finder.Find("BTCUSDT", "60", longCandles, rules, 25m, 3);
    Equal("Long", longCandidate.Direction);
    Equal(59.9m, longCandidate.Floor);
    Equal(160m, longCandidate.Ceiling);

    var shortPrices = Enumerable.Range(0, 100).Select(_ => 100m).ToArray();
    for (var offset = -3; offset <= 3; offset++)
    {
        shortPrices[20 + offset] = 140m - Math.Abs(offset);
        shortPrices[60 + offset] = 70m + Math.Abs(offset);
    }
    var shortCandles = shortPrices.Select((price, index) => new BybitKline(index * 60_000L,
        price, index == 1 ? 180m : price + 0.1m, index == 68 ? 50m : price - 0.1m, price, 1m)).ToArray();
    var shortCandidate = finder.Find("BTCUSDT", "60", shortCandles, rules, 25m, 3);
    Equal("Short", shortCandidate.Direction);
    Equal(140.1m, shortCandidate.Ceiling);
    Equal(50m, shortCandidate.Floor);
});

Run("Signal Lab moves Short ceiling after an intermediate 61.8 percent entry was touched", () =>
{
    var prices = Enumerable.Range(0, 120).Select(_ => 120m).ToArray();
    void Pivot(int center, decimal price, bool high)
    {
        for (var offset = -3; offset <= 3; offset++)
            prices[center + offset] = high ? price - Math.Abs(offset) : price + Math.Abs(offset);
    }
    Pivot(18, 150m, true);
    Pivot(38, 110m, false);
    Pivot(55, 140m, true); // above the logarithmic 61.8% entry of the first range
    Pivot(82, 80m, false);
    var candles = prices.Select((price, index) => new BybitKline(index * 60_000L,
        price, price + 0.1m, price - 0.1m, price, 1m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var candidate = new SignalCandidateFinder(new StrategyCalculator())
        .Find("BTCUSDT", "60", candles, rules, 25m, 3, useClosePrices: true);
    Equal("Short", candidate.Direction);
    Equal(140m, candidate.Ceiling);
    Equal(80m, candidate.Floor);
    True(candidate.IsBurned);
});

Run("Signal Lab major floor follows the visible chart range", () =>
{
    var prices = Enumerable.Range(0, 140).Select(i => 110m + i * 0.03m).ToArray();
    void Pivot(int center, decimal price, bool high)
    {
        for (var offset = -3; offset <= 3; offset++)
            prices[center + offset] = high ? price - Math.Abs(offset) : price + Math.Abs(offset);
    }
    Pivot(20, 80m, false); Pivot(38, 130m, true); // old wide-range anchors
    Pivot(75, 105m, false); Pivot(96, 130m, true); Pivot(116, 111m, false);
    var candles = prices.Select((price, index) => new BybitKline(index * 60_000L,
        price, price + 0.1m, price - 0.1m, price, 1m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var finder = new SignalCandidateFinder(new StrategyCalculator());
    var full = finder.Find("BTCUSDT", "240", candles, rules, 25m, 3);
    var visible = finder.Find("BTCUSDT", "240", candles.Skip(60).ToArray(), rules, 25m, 3);
    True(visible.Floor > full.Floor);
    Equal(full.Ceiling, visible.Ceiling);
    Equal(80, visible.RangeCandleCount);
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
static void Near(decimal expected, decimal actual, decimal tolerance = 0.000001m)
{
    if (Math.Abs(expected - actual) > tolerance)
        throw new Exception($"Expected {expected} ± {tolerance}, got {actual}.");
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

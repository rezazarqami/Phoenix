using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;
using Phoenix.Web;

var passed = 0;
var failed = 0;

Run("Public lifecycle notifications require publication and exclude initial expiry", () =>
{
    var sent = new List<string>();
    var notifier = new PublicSignalNotifier(new("test-token", "test-chat"),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicSignalNotifier>.Instance,
        new HttpClient(new StubHttpHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = System.Text.Json.JsonDocument.Parse(body);
            Equal(123, json.RootElement.GetProperty("reply_parameters").GetProperty("message_id").GetInt32());
            sent.Add(json.RootElement.GetProperty("text").GetString()!);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":456}}") };
        })));
    var s = new ServerSignal { Symbol = "BTCUSDT", FilledAtUtc = DateTime.UtcNow,
        CompletedAtUtc = DateTime.UtcNow, Outcome = "Expired", ExpireReason = "TargetAfterActivation" };
    notifier.OpenedAsync(s, default).GetAwaiter().GetResult();
    notifier.RiskFreeClosedAsync(s, default).GetAwaiter().GetResult();
    notifier.ExpiredAsync(s, default).GetAwaiter().GetResult();
    Equal(0, sent.Count);
    Equal(0, PublicSignalNotificationWorker.Events(s).Count());
    s.PublicTelegramMessageId = 123;
    s.ExpireReason = "InitialBoundary";
    notifier.ExpiredAsync(s, default).GetAwaiter().GetResult();
    Equal(0, sent.Count);
    False(PublicSignalNotificationWorker.Events(s).Any(x => x.Kind == "Expired"));
    s.ExpireReason = "TargetAfterActivation";
    notifier.ExpiredAsync(s, default).GetAwaiter().GetResult();
    notifier.OpenedAsync(s, default).GetAwaiter().GetResult();
    notifier.RiskFreeClosedAsync(s, default).GetAwaiter().GetResult();
    Equal(3, sent.Count);
    True(sent.All(x => !x.Contains("موجودی") && !x.Contains("کیف پول")));
    True(PublicSignalNotificationWorker.Events(s).Any(x => x.Kind == "Expired"));
});

Run("Dedicated account signals use their own bot and results-only lifecycle", () =>
{
    var destination = string.Empty;
    var chatId = string.Empty;
    var dedicated = new DedicatedTelegramOptions("arman", "dedicated-token", "777");
    var notifier = new PublicSignalNotifier(new("public-token", "111"), dedicated,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicSignalNotifier>.Instance,
        new HttpClient(new StubHttpHandler(request =>
        {
            destination = request.RequestUri!.ToString();
            using var json = System.Text.Json.JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            chatId = json.RootElement.GetProperty("chat_id").GetString()!;
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":456}}")
            };
        })));
    var now = DateTime.UtcNow;
    var signal = new ServerSignal
    {
        Symbol = "ETHUSDT", Direction = "Long", RequestedByUsername = "ArMaN",
        PublicSignalNumber = 9, PublicTelegramMessageId = 456, FilledAtUtc = now,
        RiskFreeReachedAtUtc = now.AddMinutes(1), CompletedAtUtc = now.AddMinutes(2), Outcome = "Target"
    };
    True(dedicated.Owns(signal.RequestedByUsername));
    True(notifier.IsDedicatedSignal(signal));
    notifier.PublishAsync(signal, default).GetAwaiter().GetResult();
    True(destination.Contains("botdedicated-token/sendMessage"));
    Equal("777", chatId);
    var events = PublicSignalNotificationWorker.Events(signal, resultsOnly: true).Select(x => x.Kind).ToArray();
    False(events.Contains("Opened"));
    True(events.Contains("RiskFreeReached"));
    True(events.Contains("Target"));
});

Run("Dedicated bot securely pairs one second chat and sends both copies", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "phoenix-dedicated-chat-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var chats = new List<string>();
        var options = new DedicatedTelegramOptions("arman", "dedicated-token", "777", "pair-123",
            Path.Combine(root, "chats.json"));
        var dedicated = new DedicatedTelegramNotifier(options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DedicatedTelegramNotifier>.Instance,
            new HttpClient(new StubHttpHandler(_ => new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":456}}")
            })));
        var paired = dedicated.TryPairAsync(
            new TelegramCommand(1, "888", 888, "arman", "Arman", "/start pair-123", null), default)
            .GetAwaiter().GetResult();
        True(paired);
        True(dedicated.IsAuthorized(new TelegramCommand(2, "777", 777, null, "Owner", "/start", null)));
        True(dedicated.IsAuthorized(new TelegramCommand(3, "888", 888, null, "Arman", "/start", null)));

        var notifier = new PublicSignalNotifier(new("public-token", "111"), options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicSignalNotifier>.Instance,
            new HttpClient(new StubHttpHandler(request =>
            {
                using var json = System.Text.Json.JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                chats.Add(json.RootElement.GetProperty("chat_id").GetString()!);
                return new(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":456}}")
                };
            })));
        notifier.PublishAsync(new ServerSignal
        {
            Symbol = "BTCUSDT", Direction = "Long", RequestedByUsername = "arman",
            PublicSignalNumber = 10, EntryPrice = 1, TakeProfit = 2, StopLoss = 0.5m
        }, default).GetAwaiter().GetResult();
        Equal(2, chats.Count);
        True(chats.Contains("777"));
        True(chats.Contains("888"));
    }
    finally { Directory.Delete(root, true); }
});

Run("Signal requester survives queue persistence for Telegram routing", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "phoenix-owner-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var queue = Path.Combine(root, "queue.json");
        var history = Path.Combine(root, "history.db");
        var signal = new ServerSignal
        {
            Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = "Long", Status = "Pending",
            RequestedByUsername = "arman", CreatedAtUtc = DateTime.UtcNow
        };
        new ServerOrderStore(queue, history).AddAsync(signal).GetAwaiter().GetResult();
        var restored = new ServerOrderStore(queue, history).GetAllAsync().GetAwaiter().GetResult().Single();
        Equal("arman", restored.RequestedByUsername!);
    }
    finally { SignalHistoryStore.ClearConnectionPools(); Directory.Delete(root, true); }
});

Run("Bulk cancellation is direction-filtered and cannot cancel filled or claimed entries", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "phoenix-bulk-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var store = new ServerOrderStore(Path.Combine(root, "queue.json"), Path.Combine(root, "history.db"));
        var signals = new[] {
            new ServerSignal { Id = Guid.NewGuid(), Direction = "Long", Status = "Pending" },
            new ServerSignal { Id = Guid.NewGuid(), Direction = "Short", Status = "Pending" },
            new ServerSignal { Id = Guid.NewGuid(), Direction = "Long", Status = "Filled" },
            new ServerSignal { Id = Guid.NewGuid(), Direction = "Long", Status = "Submitting" }
        };
        foreach (var s in signals) { s.Symbol = "BTCUSDT"; s.CreatedAtUtc = DateTime.UtcNow; store.AddAsync(s).GetAwaiter().GetResult(); }
        Equal(1, store.CancelPendingAsync("Long").GetAwaiter().GetResult());
        False(store.TryClaimSubmissionAsync(signals[0].Id, 100).GetAwaiter().GetResult());
        store.UpdateAsync(signals[0]).GetAwaiter().GetResult();
        Equal("Cancelled", store.GetAllAsync().GetAwaiter().GetResult().Single(x => x.Id == signals[0].Id).Status);
        store.SetEntriesPausedAsync(true).GetAwaiter().GetResult();
        False(store.TryClaimSubmissionAsync(signals[1].Id, 100).GetAwaiter().GetResult());
        True(new ServerOrderStore(Path.Combine(root, "queue.json"), Path.Combine(root, "history.db")).EntriesPaused);
        Equal(1, store.CancelPendingAsync("All").GetAwaiter().GetResult());
        Equal(1, store.GetAllAsync().GetAwaiter().GetResult().Count(x => x.Status == "Filled"));
        Equal(1, store.GetAllAsync().GetAwaiter().GetResult().Count(x => x.Status == "Submitting"));
    }
    finally { SignalHistoryStore.ClearConnectionPools(); Directory.Delete(root, true); }
});

Run("Close positions uses paginated exchange quantities and reduce-only market orders", () =>
{
    var pages = 0;
    var closes = 0;
    var client = new BybitDemoClient(new("test", "test"), new HttpClient(new StubHttpHandler(request =>
    {
        if (request.Method == HttpMethod.Get)
        {
            pages++;
            if (pages == 2) True(request.RequestUri!.Query.Contains("cursor=page2"));
            var body = pages == 1
                ? "{\"retCode\":0,\"result\":{\"list\":[{\"symbol\":\"BTCUSDT\",\"side\":\"Buy\",\"size\":\"2\",\"positionIdx\":1}],\"nextPageCursor\":\"page2\"}}"
                : "{\"retCode\":0,\"result\":{\"list\":[{\"symbol\":\"BTCUSDT\",\"side\":\"Sell\",\"size\":\"3\",\"positionIdx\":2}],\"nextPageCursor\":\"\"}}";
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        }
        closes++;
        using var json = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var p = json.RootElement;
        True(p.GetProperty("reduceOnly").GetBoolean());
        Equal("Market", p.GetProperty("orderType").GetString()!);
        Equal(closes == 1 ? "Sell" : "Buy", p.GetProperty("side").GetString()!);
        Equal(closes, p.GetProperty("positionIdx").GetInt32());
        Equal(closes == 1 ? "2" : "3", p.GetProperty("qty").GetString()!);
        False(p.TryGetProperty("takeProfit", out _));
        return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"retCode\":0,\"result\":{\"orderId\":\"close-test\"}}") };
    })));
    var positions = client.GetOpenPositionsAsync().GetAwaiter().GetResult();
    Equal(2, positions.Count);
    foreach (var p in positions) client.ClosePositionAsync(p).GetAwaiter().GetResult();
    Equal(2, closes);
});

Run("Bulk close requires one-use confirmation, pauses entries and does not assume a fill", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "phoenix-close-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var orders = 0;
        var client = new BybitDemoClient(new("test", "test"), new HttpClient(new StubHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post) orders++;
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Get
                ? "{\"retCode\":0,\"result\":{\"list\":[{\"symbol\":\"BTCUSDT\",\"side\":\"Buy\",\"size\":\"2\",\"positionIdx\":0}],\"nextPageCursor\":\"\"}}"
                : "{\"retCode\":0,\"result\":{\"orderId\":\"manual-close\"}}") };
        })));
        var store = new ServerOrderStore(Path.Combine(root, "q.json"), Path.Combine(root, "h.db"));
        var s = new ServerSignal { Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = "Long", Status = "Filled", CreatedAtUtc = DateTime.UtcNow };
        store.AddAsync(s).GetAwaiter().GetResult();
        var service = new BulkPositionService(store, client, new("test", "test"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BulkPositionService>.Instance);
        var preview = service.PreviewAsync(default).GetAwaiter().GetResult();
        Equal(0, orders);
        False(store.EntriesPaused);
        var result = service.CloseAsync(preview.Id, default).GetAwaiter().GetResult();
        True(result.Single().Submitted);
        Equal(1, orders);
        True(store.EntriesPaused);
        var closing = store.GetAllAsync().GetAwaiter().GetResult().Single();
        Equal("Closing", closing.Status);
        True(closing.CompletedAtUtc is null);
        var rejected = false;
        try { service.CloseAsync(preview.Id, default).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { rejected = true; }
        True(rejected);
        Equal(1, orders);
    }
    finally { SignalHistoryStore.ClearConnectionPools(); Directory.Delete(root, true); }
});

Run("Analysis accepts only the shared Phoenix session and preserves roles", () =>
{
    var previousUser = Environment.GetEnvironmentVariable("PHOENIX_AUTH_USERNAME");
    var previousPassword = Environment.GetEnvironmentVariable("PHOENIX_AUTH_PASSWORD");
    try
    {
        Environment.SetEnvironmentVariable("PHOENIX_AUTH_USERNAME", "test-admin");
        Environment.SetEnvironmentVariable("PHOENIX_AUTH_PASSWORD", "test-only-password");
        foreach (var role in new[] { "admin", "viewer", "editor" })
        {
            var username = role == "admin" ? "test-admin" : "test-user";
            var token = PhoenixSessionAuth.CreateToken(username, role == "viewer", role == "admin");
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Headers.Cookie = PhoenixSessionAuth.CookieName + "=" + token;
            True(AnalysisSessionAuth.TryGetIdentity(context.Request, out var identity));
            Equal(username, identity.Username);
            Equal(role == "viewer", identity.ViewerOnly);
            Equal(role == "admin", identity.IsAdmin);
            var legacy = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            legacy.Request.Headers.Cookie = AnalysisSessionAuth.CookieName + "=" + token;
            False(AnalysisSessionAuth.TryGetIdentity(legacy.Request, out _));
        }
        var original = PhoenixSessionAuth.CreateToken("test-admin", false, true);
        var stale = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        stale.Request.Headers.Cookie = PhoenixSessionAuth.CookieName + "=" + original;
        Environment.SetEnvironmentVariable("PHOENIX_AUTH_PASSWORD", "rotated-test-password");
        False(PhoenixSessionAuth.TryGetIdentity(stale.Request, out _));
        False(AnalysisSessionAuth.TryGetIdentity(stale.Request, out _));
    }
    finally
    {
        Environment.SetEnvironmentVariable("PHOENIX_AUTH_USERNAME", previousUser);
        Environment.SetEnvironmentVariable("PHOENIX_AUTH_PASSWORD", previousPassword);
    }
});

Run("Wallet notifications use USDT wallet balance, not available USD or equity", () =>
{
    var client = new BybitDemoClient(new BybitDemoOptions("test-key", "test-secret"),
        new HttpClient(new StubHttpHandler(request =>
        {
            Equal("/v5/account/wallet-balance", request.RequestUri!.AbsolutePath);
            Equal("?accountType=UNIFIED&coin=USDT", request.RequestUri.Query);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                "{\"retCode\":0,\"result\":{\"list\":[{\"totalAvailableBalance\":\"12\",\"totalEquity\":\"500\",\"coin\":[{\"coin\":\"USDT\",\"walletBalance\":\"123.45\"}]}]}}") };
        })));
    Equal(123.45m, client.GetUsdtWalletBalanceAsync().GetAwaiter().GetResult());
    True(WalletNotification.ReadAsync(client, default).GetAwaiter().GetResult().Contains("123.45 USDT"));
});

Run("Missing wallet balance is not reported as zero and does not block notification", () =>
{
    var client = new BybitDemoClient(new BybitDemoOptions("test-key", "test-secret"),
        new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("{\"retCode\":0,\"result\":{\"list\":[{\"coin\":[]}]}}") })));
    var text = WalletNotification.ReadAsync(client, default).GetAwaiter().GetResult();
    True(text.Contains("دریافت نشد"));
    False(text.Contains("0 USDT"));
});

Run("Review archive preserves rejected, approved and unanswered images across restart", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"phoenix-review-{Guid.NewGuid():N}.db");
    try
    {
        var archive = new ReviewArchiveStore(path);
        var candidate = new SignalCandidate("BTCUSDT", "5", "Long", 120, 80, 101, 100, 110, 90,
            102.5m, 107.5m, 5, 1, 80, 1, 2, 0, 3, 4, "test", false, null);
        var candles = new[] { new BybitKline(0, 100, 101, 99, 100, 10) };
        byte[] image = [137, 80, 78, 71, 3, 4];
        foreach (var key in new[] { "approved", "rejected", "unanswered" })
        {
            archive.SaveAsync(key, candidate, candles, "5", false, image, default).GetAwaiter().GetResult();
            archive.MarkDeliveryAsync(key, true, default).GetAwaiter().GetResult();
        }
        True(archive.DecideAsync("approved", true, default).GetAwaiter().GetResult());
        True(archive.DecideAsync("rejected", false, default).GetAwaiter().GetResult());
        False(archive.DecideAsync("rejected", true, default).GetAwaiter().GetResult());
        False(archive.DecideAsync("unknown", true, default).GetAwaiter().GetResult());
        var zip = new ReviewArchiveStore(path).ExportAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), default).GetAwaiter().GetResult();
        using var stream = new MemoryStream(zip);
        using var result = new System.IO.Compression.ZipArchive(stream);
        foreach (var folder in new[] { "Approved/approved", "Rejected/rejected", "Unanswered/unanswered" })
        {
            using var png = result.GetEntry(folder + ".png")!.Open();
            using var saved = new MemoryStream(); png.CopyTo(saved);
            True(image.SequenceEqual(saved.ToArray()));
            True(result.GetEntry(folder + "-candles.json") is not null);
        }
        using var manifestReader = new StreamReader(result.GetEntry("manifest.json")!.Open());
        var manifest = manifestReader.ReadToEnd();
        True(manifest.Contains("Rejected")); True(manifest.Contains("timeframe"));
    }
    finally
    {
        SignalHistoryStore.ClearConnectionPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }
});

Run("Long strategy calculates expected levels", () =>
{
    var signal = BuildSignal(Direction.Long);
    var plan = new StrategyCalculator().Calculate(signal);
    var effectiveLow = signal.Low + (signal.High - signal.Low) * 0.01m;
    var entry = StrategyCalculator.LogarithmicLevel(effectiveLow, signal.High, 1m - 0.618m);
    var target = StrategyCalculator.LogarithmicLevel(effectiveLow, signal.High, 1m - 0.500m);
    var stop = StrategyCalculator.LogarithmicLevel(effectiveLow, signal.High, 1m - 0.729m);
    Near(entry, plan.EntryPrice);
    Near(target, plan.TakeProfit);
    Near(stop, plan.StopLoss1);
    Near(entry + (target - entry) * 0.50m, plan.StopLoss2!.Value);
    Near(entry + (target - entry) * 0.75m, plan.RiskFreePrice);
});

Run("Short strategy calculates expected levels", () =>
{
    var signal = BuildSignal(Direction.Short);
    var plan = new StrategyCalculator().Calculate(signal);
    var effectiveHigh = signal.High - (signal.High - signal.Low) * 0.01m;
    var entry = StrategyCalculator.LogarithmicLevel(signal.Low, effectiveHigh, 0.618m);
    var target = StrategyCalculator.LogarithmicLevel(signal.Low, effectiveHigh, 0.500m);
    var stop = StrategyCalculator.LogarithmicLevel(signal.Low, effectiveHigh, 0.729m);
    Near(entry, plan.EntryPrice);
    Near(target, plan.TakeProfit);
    Near(stop, plan.StopLoss1);
    Near(entry + (target - entry) * 0.50m, plan.StopLoss2!.Value);
    Near(entry + (target - entry) * 0.75m, plan.RiskFreePrice);
});

Run("Directional range adjustment moves only the side nearest entry", () =>
{
    Equal((118020m, 120000m), StrategyCalculator.AdjustRangeForDirection(118000m, 120000m, Direction.Long));
    Equal((118000m, 119980m), StrategyCalculator.AdjustRangeForDirection(118000m, 120000m, Direction.Short));
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
    Equal(BybitOrderPreviewBuilder.FloorToStep(position.Quantity, rules.QuantityStep), preview.Quantity);
    Equal(BybitOrderPreviewBuilder.RoundToStep(position.EntryPrice, rules.TickSize), preview.Price);
    Equal(BybitOrderPreviewBuilder.RoundToStep(position.TakeProfit, rules.TickSize), preview.TakeProfit);
    Equal(BybitOrderPreviewBuilder.RoundToStep(position.StopLoss1, rules.TickSize), preview.StopLoss);
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
        var ranged = new ServerOrderStore().GetHistoryRangeAsync(
            signal.CreatedAtUtc.AddMinutes(-1), signal.CreatedAtUtc.AddMinutes(1)).GetAwaiter().GetResult();
        Equal(1, ranged.Count);
        Equal(signal.Id, ranged[0].Signal.Id);
        Equal(0, new ServerOrderStore().GetHistoryRangeAsync(
            signal.CreatedAtUtc.AddDays(1), signal.CreatedAtUtc.AddDays(2)).GetAwaiter().GetResult().Count);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH", previous);
        Environment.SetEnvironmentVariable("PHOENIX_HISTORY_DB_PATH", previousHistory);
        SignalHistoryStore.ClearConnectionPools();
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        if (File.Exists(historyPath)) File.Delete(historyPath);
        if (File.Exists(historyPath + "-wal")) File.Delete(historyPath + "-wal");
        if (File.Exists(historyPath + "-shm")) File.Delete(historyPath + "-shm");
    }
});

Run("Signal evidence is stored and initial expiry removes its details", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"phoenix-evidence-{Guid.NewGuid():N}.json");
    var historyPath = Path.ChangeExtension(path, ".db");
    try
    {
        var store = new ServerOrderStore(path, historyPath);
        var signal = new ServerSignal
        {
            Id = Guid.NewGuid(), Symbol = "ETHUSDT", Direction = "Short", Quantity = 0.01m,
            EntryPrice = 100m, TakeProfit = 90m, StopLoss = 110m, OrderLinkId = "evidence-test",
            CreatedAtUtc = DateTime.UtcNow, Timeframe = "15", ChartMode = "Candles"
        };
        var image = new byte[] { 137, 80, 78, 71, 1, 2, 3 };
        store.AddAsync(signal, evidence: new SignalEvidence("15", "Candles", image)).GetAwaiter().GetResult();
        True(store.GetHistoryImageAsync(signal.Id).GetAwaiter().GetResult()!.SequenceEqual(image));
        var retained = store.GetHistoryRangeAsync(signal.CreatedAtUtc.AddMinutes(-1), signal.CreatedAtUtc.AddMinutes(1)).GetAwaiter().GetResult().Single();
        Equal("15", retained.Signal.Timeframe!);
        True(retained.HasImage);

        signal.Status = "Expired"; signal.Outcome = "Expired"; signal.ExpireReason = "InitialBoundary";
        signal.ExpiredAtUtc = signal.CompletedAtUtc = DateTime.UtcNow;
        store.UpdateAsync(signal).GetAwaiter().GetResult();
        True(store.GetHistoryImageAsync(signal.Id).GetAwaiter().GetResult() is null);
        var compact = store.GetHistoryRangeAsync(signal.CreatedAtUtc.AddMinutes(-1), signal.CreatedAtUtc.AddMinutes(1)).GetAwaiter().GetResult().Single();
        Equal("Expired", compact.Signal.Outcome!);
        Equal("InitialBoundary", compact.Signal.ExpireReason!);
        Equal("DELETED", compact.Signal.Symbol);
        False(compact.HasImage);
    }
    finally
    {
        SignalHistoryStore.ClearConnectionPools();
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
        SignalHistoryStore.ClearConnectionPools();
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
        SignalHistoryStore.ClearConnectionPools();
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
        SignalHistoryStore.ClearConnectionPools();
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
    var snapshot = SignalChartRenderer.Render(candles, candidate, false);
    True(snapshot.Length > 1000);
    True(snapshot.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
    var labeledSnapshot = SignalChartRenderer.Render(candles, candidate, false, "1H");
    True(labeledSnapshot.Length > 1000);
    False(snapshot.SequenceEqual(labeledSnapshot));
    Equal("15", SignalBatchService.ReviewChartInterval("5"));
    Equal("15", SignalBatchService.ReviewChartInterval("15"));
    Equal("60", SignalBatchService.ReviewChartInterval("60"));
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

Run("Signal Lab checks every candle sequentially for a 61.8 percent reset", () =>
{
    var prices = Enumerable.Range(0, 100).Select(_ => 100m).ToArray();
    for (var offset = -5; offset <= 5; offset++)
    {
        prices[12 + offset] = 80m + Math.Abs(offset);
        prices[72 + offset] = 130m - Math.Abs(offset);
    }
    prices[23] = 96m; prices[24] = 98m; prices[25] = 100m; prices[26] = 94m;
    prices[27] = 85m; prices[28] = 96m; prices[29] = 105m; prices[30] = 101m;
    var candles = prices.Select((price, index) => new BybitKline(index * 60_000L,
        price, price, price, price, 1m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var candidate = new SignalCandidateFinder(new StrategyCalculator())
        .Find("BTCUSDT", "60", candles, rules, 25m, 5, useClosePrices: true);
    Equal("Long", candidate.Direction);
    Equal(85m, candidate.Floor);
    Equal(130m, candidate.Ceiling);
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

Run("Signal Lab detects a 61.8 percent reset without a strict local pivot", () =>
{
    var prices = Enumerable.Range(0, 120).Select(_ => 120m).ToArray();
    void Pivot(int center, decimal price, bool high)
    {
        for (var offset = -3; offset <= 3; offset++)
            prices[center + offset] = high ? price - Math.Abs(offset) : price + Math.Abs(offset);
    }
    Pivot(18, 150m, true);
    prices[36] = 114m; prices[37] = 112m; prices[38] = 110m; prices[39] = 110m;
    prices[40] = 112m; prices[41] = 115m; // equal lows deliberately defeat strict pivot detection
    prices[53] = 137m; prices[54] = 140m; prices[55] = 140m; prices[56] = 137m;
    Pivot(82, 80m, false);
    var candles = prices.Select((price, index) => new BybitKline(index * 300_000L,
        price, price, price, price, 1m)).ToArray();
    var rules = new BybitInstrumentRules("BTCUSDT", 0.1m, 0.001m, 0.001m, 5m, 100m, 1m, 0.01m);
    var candidate = new SignalCandidateFinder(new StrategyCalculator())
        .Find("BTCUSDT", "5", candles, rules, 25m, 3, useClosePrices: true);
    Equal("Short", candidate.Direction);
    Equal(140m, candidate.Ceiling);
    Equal(80m, candidate.Floor);
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

Run("Line proposals are rejected when only a candle wick touched entry", () =>
{
    var candles = new[]
    {
        new BybitKline(1, 105m, 106m, 104m, 105m, 1m),
        new BybitKline(2, 105m, 106m, 99.9m, 105m, 1m)
    };
    var result = SignalQualityAssessment.PostFormationValidity("Long", 100m, 110m, 1, candles);
    True(result.Invalid);
    Equal("EntryTouchedByWick", result.Reason);
});

Run("Proposal expires after approaching entry and returning to target", () =>
{
    var candles = new[]
    {
        new BybitKline(2, 104m, 105m, 102.5m, 104m, 1m),
        new BybitKline(3, 108m, 110m, 107m, 110m, 1m)
    };
    var result = SignalQualityAssessment.PostFormationValidity("Long", 100m, 110m, 1, candles);
    True(result.Invalid);
    Equal("TargetAfterActivation", result.Reason);
});

Run("Very weak impulse filter preserves the learned conservative boundary", () =>
{
    var candidate = new SignalCandidate("BTCUSDT", "5", "Long", 106m, 100m, 104m,
        102m, 103m, 101m, null, null, 1m, 1m, 50m, 1, 3, 1, 4, 4, "", false, null);
    var weak = new[]
    {
        new BybitKline(1,100m,101m,99m,100m,1m),
        new BybitKline(2,200m,201m,199m,200m,1m),
        new BybitKline(3,0.1m,1m,0.1m,0.1m,1m),
        new BybitKline(4,106m,107m,105m,106m,1m)
    };
    True(SignalQualityAssessment.ImpulseEfficiency(candidate, weak) < 0.06m);
    True(SignalQualityAssessment.IsVeryWeakImpulse(candidate, weak));
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

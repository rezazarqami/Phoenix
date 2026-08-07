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
    var options = new BybitTestnetOptions("test-key", "test-secret");
    var client = new BybitTestnetClient(options, timestampProvider: () => 1_700_000_000_000);
    using var request = client.CreateSignedGetRequest("/v5/account/wallet-balance", "accountType=UNIFIED&coin=USDT");
    Equal("test-key", request.Headers.GetValues("X-BAPI-API-KEY").Single());
    Equal("1700000000000", request.Headers.GetValues("X-BAPI-TIMESTAMP").Single());
    Equal("5000", request.Headers.GetValues("X-BAPI-RECV-WINDOW").Single());
    Equal(64, request.Headers.GetValues("X-BAPI-SIGN").Single().Length);
});

Run("Bybit private requests require Testnet credentials", () =>
{
    var client = new BybitTestnetClient(new BybitTestnetOptions(null, null));
    Throws<InvalidOperationException>(() =>
        client.CreateSignedGetRequest("/v5/account/wallet-balance", "accountType=UNIFIED"));
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

using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;

Signal signal = new()
{
    Id = Guid.NewGuid(),

    Symbol = "BTCUSDT",

    Direction = Direction.Long,

    High = 120000m,

    Low = 118000m,

    PositionSizeUsdt = 100m,

    Status = SignalStatus.WaitingEntry,

    CreatedAt = DateTime.UtcNow
};

StrategyCalculator calculator = new();

CalculationService calculationService = new(calculator);

SignalManager signalManager = new(calculationService);

bool added = signalManager.AddSignal(signal);

EntryManager entryManager = new();

bool canOpen = entryManager.CanOpenPosition(signal, 118700m);

Console.WriteLine($"Added : {added}");

Console.WriteLine($"Can Open : {canOpen}");

if (canOpen)
{
    ExecutionManager executionManager = new();

    Position? position = executionManager.OpenPosition(signal);

    if (position != null)
    {
        Console.WriteLine();

        Console.WriteLine("===== POSITION OPENED =====");

        Console.WriteLine($"Position Id : {position.Id}");

        Console.WriteLine($"Signal Id   : {position.SignalId}");

        Console.WriteLine($"Entry Price : {position.EntryPrice}");

        Console.WriteLine($"Quantity    : {position.Quantity}");

        Console.WriteLine($"Status      : {position.Status}");

        Console.WriteLine();

        TestExchange exchange = new();

        OrderManager orderManager = new(exchange);

        orderManager.PlaceOrders(position);
    }
}
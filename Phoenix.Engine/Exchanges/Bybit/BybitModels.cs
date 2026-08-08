namespace Phoenix.Engine.Exchanges.Bybit;

public sealed record BybitTicker(string Symbol, decimal LastPrice, DateTime ReceivedAtUtc);

public sealed record BybitTestnetStatus(
    bool PublicApiAvailable,
    bool Authenticated,
    decimal? TotalEquityUsd,
    string Message);

public sealed record BybitInstrumentRules(
    string Symbol,
    decimal TickSize,
    decimal QuantityStep,
    decimal MinimumOrderQuantity,
    decimal MinimumNotionalValue);

public sealed record BybitOrderPreview(
    string Symbol,
    string Side,
    decimal Quantity,
    decimal TakeProfit,
    decimal StopLoss,
    decimal EstimatedNotional);

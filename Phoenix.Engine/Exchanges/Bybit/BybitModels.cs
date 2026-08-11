namespace Phoenix.Engine.Exchanges.Bybit;

public sealed record BybitTicker(string Symbol, decimal LastPrice, DateTime ReceivedAtUtc);

public sealed record BybitDemoStatus(
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
    decimal Price,
    decimal TakeProfit,
    decimal StopLoss,
    decimal EstimatedNotional);

public sealed record BybitOrderResult(
    string OrderId,
    string OrderLinkId,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal Price);

public sealed record BybitCancelResult(string OrderId, string OrderLinkId);

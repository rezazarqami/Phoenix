namespace Phoenix.Engine.Exchanges.Bybit;

public sealed record BybitTicker(string Symbol, decimal LastPrice, DateTime ReceivedAtUtc);

public sealed record BybitTestnetStatus(
    bool PublicApiAvailable,
    bool Authenticated,
    decimal? TotalEquityUsd,
    string Message);

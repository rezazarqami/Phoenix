using Phoenix.Core.Entities;

namespace Phoenix.Engine.Exchanges.Bybit;

public static class BybitOrderPreviewBuilder
{
    public static BybitOrderPreview Build(string symbol, Position position, BybitInstrumentRules rules)
    {
        var quantity = FloorToStep(position.Quantity, rules.QuantityStep);
        var takeProfit = RoundToStep(position.TakeProfit, rules.TickSize);
        var stopLoss = RoundToStep(position.StopLoss1, rules.TickSize);
        var notional = quantity * position.EntryPrice;

        if (quantity < rules.MinimumOrderQuantity)
            throw new InvalidOperationException($"Quantity {quantity} is below Bybit minimum {rules.MinimumOrderQuantity}.");
        if (notional < rules.MinimumNotionalValue)
            throw new InvalidOperationException($"Order value {notional} is below Bybit minimum {rules.MinimumNotionalValue}.");

        return new BybitOrderPreview(
            symbol.Trim().ToUpperInvariant(),
            position.Direction == Direction.Long ? "Buy" : "Sell",
            quantity,
            takeProfit,
            stopLoss,
            notional);
    }

    public static decimal FloorToStep(decimal value, decimal step)
    {
        if (value <= 0 || step <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Value and step must be positive.");
        return decimal.Floor(value / step) * step;
    }

    public static decimal RoundToStep(decimal value, decimal step)
    {
        if (value <= 0 || step <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Value and step must be positive.");
        return decimal.Round(value / step, 0, MidpointRounding.AwayFromZero) * step;
    }
}

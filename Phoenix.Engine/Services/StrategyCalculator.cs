using Phoenix.Core.Entities;
using Phoenix.Engine.Interfaces;

namespace Phoenix.Engine.Services;

public class StrategyCalculator : IStrategyCalculator
{
    public TradePlan Calculate(Signal signal)
    {
        if (signal.High <= signal.Low)
            throw new ArgumentException("High price must be greater than low price.", nameof(signal));

        if (signal.PositionSizeUsdt <= 0)
            throw new ArgumentException("Position size must be greater than zero.", nameof(signal));

        TradePlan plan = new();

        if (signal.Direction == Direction.Long)
        {
            plan.EntryPrice = LogarithmicLevel(signal.Low, signal.High, 1m - 0.618m);
            plan.TakeProfit = LogarithmicLevel(signal.Low, signal.High, 1m - 0.500m);
            plan.StopLoss1 = LogarithmicLevel(signal.Low, signal.High, 1m - 0.729m);

            var profitDistance = plan.TakeProfit - plan.EntryPrice;
            plan.StopLoss2 = plan.EntryPrice + profitDistance * 0.25m;
            plan.RiskFreePrice = plan.EntryPrice + profitDistance * 0.75m;
        }
        else
        {
            plan.EntryPrice = LogarithmicLevel(signal.Low, signal.High, 0.618m);
            plan.TakeProfit = LogarithmicLevel(signal.Low, signal.High, 0.500m);
            plan.StopLoss1 = LogarithmicLevel(signal.Low, signal.High, 0.729m);

            var profitDistance = plan.TakeProfit - plan.EntryPrice;
            plan.StopLoss2 = plan.EntryPrice + profitDistance * 0.25m;
            plan.RiskFreePrice = plan.EntryPrice + profitDistance * 0.75m;
        }

        plan.Leverage = CalculateLeverage(plan.EntryPrice, plan.TakeProfit);

        return plan;
    }

    public static decimal LogarithmicLevel(decimal low, decimal high, decimal fractionFromLow)
    {
        if (low <= 0m || high <= low)
            throw new ArgumentOutOfRangeException(nameof(low), "Logarithmic levels require 0 < low < high.");
        if (fractionFromLow is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(fractionFromLow));

        var logLow = Math.Log((double)low);
        var logHigh = Math.Log((double)high);
        return (decimal)Math.Exp(logLow + (double)fractionFromLow * (logHigh - logLow));
    }

    public static decimal CalculateLeverage(decimal entryPrice, decimal takeProfit)
    {
        if (entryPrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(entryPrice));
        var targetDistancePercent = Math.Abs(entryPrice - takeProfit) / entryPrice * 100m;
        if (targetDistancePercent <= 0m)
            throw new InvalidOperationException("Entry-to-target distance must be greater than zero.");
        return 50m / targetDistancePercent;
    }
}

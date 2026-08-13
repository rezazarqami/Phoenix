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

        decimal range = signal.High - signal.Low;

        decimal x = range * 0.618m;
        decimal y = range * 0.500m;
        decimal z = range * 0.729m;

        TradePlan plan = new();

        if (signal.Direction == Direction.Long)
        {
            plan.EntryPrice = signal.High - x;

            plan.TakeProfit = signal.High - y;

            plan.StopLoss1 = signal.High - z;

            var profitDistance = plan.TakeProfit - plan.EntryPrice;
            plan.StopLoss2 = plan.EntryPrice + profitDistance * 0.25m;
            plan.RiskFreePrice = plan.EntryPrice + profitDistance * 0.75m;
        }
        else
        {
            plan.EntryPrice = signal.Low + x;

            plan.TakeProfit = signal.Low + y;

            plan.StopLoss1 = signal.Low + z;

            var profitDistance = plan.TakeProfit - plan.EntryPrice;
            plan.StopLoss2 = plan.EntryPrice + profitDistance * 0.25m;
            plan.RiskFreePrice = plan.EntryPrice + profitDistance * 0.75m;
        }

        return plan;
    }
}

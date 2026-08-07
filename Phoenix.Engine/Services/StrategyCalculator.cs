using Phoenix.Core.Entities;
using Phoenix.Engine.Interfaces;

namespace Phoenix.Engine.Services;

public class StrategyCalculator : IStrategyCalculator
{
    public TradePlan Calculate(Signal signal)
    {
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

            // فعلاً همان مقدار قبلی
            plan.StopLoss2 = signal.High;

            // نقطه ورود به حالت Risk Free
            plan.RiskFreePrice = signal.High - y;
        }
        else
        {
            plan.EntryPrice = signal.Low + x;

            plan.TakeProfit = signal.Low + y;

            plan.StopLoss1 = signal.Low + z;

            // فعلاً همان مقدار قبلی
            plan.StopLoss2 = signal.Low;

            // نقطه ورود به حالت Risk Free
            plan.RiskFreePrice = signal.Low + y;
        }

        return plan;
    }
}
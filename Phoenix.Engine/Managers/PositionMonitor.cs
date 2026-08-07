using Phoenix.Core.Entities;

namespace Phoenix.Engine.Managers;

public class PositionMonitor
{
    public void Update(Position position, decimal currentPrice)
    {
        if (position.Status != SignalStatus.PositionOpen)
            return;

        if (IsTakeProfitHit(position, currentPrice))
        {
            position.Status = SignalStatus.TakeProfit;

            return;
        }

        if (IsStopLoss1Hit(position, currentPrice))
        {
            position.Status = SignalStatus.Stopped;

            return;
        }

        if (IsRiskFreeReached(position, currentPrice))
        {
            Console.WriteLine("Risk Free Reached");
        }
    }

    private bool IsTakeProfitHit(Position position, decimal currentPrice)
    {
        if (position.Direction == Direction.Long)
            return currentPrice >= position.TakeProfit;

        return currentPrice <= position.TakeProfit;
    }

    private bool IsStopLoss1Hit(Position position, decimal currentPrice)
    {
        if (position.Direction == Direction.Long)
            return currentPrice <= position.StopLoss1;

        return currentPrice >= position.StopLoss1;
    }

    private bool IsRiskFreeReached(Position position, decimal currentPrice)
    {
        if (position.Direction == Direction.Long)
            return currentPrice >= position.RiskFreePrice;

        return currentPrice <= position.RiskFreePrice;
    }
}
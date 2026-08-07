using Phoenix.Core.Entities;

namespace Phoenix.Engine.Managers;

public class PositionManager
{
    public bool IsTakeProfitHit(Position position, decimal currentPrice)
    {
        if (position.Direction == Direction.Long)
            return currentPrice >= position.TakeProfit;

        return currentPrice <= position.TakeProfit;
    }

    public bool IsStopLoss1Hit(Position position, decimal currentPrice)
    {
        if (position.Direction == Direction.Long)
            return currentPrice <= position.StopLoss1;

        return currentPrice >= position.StopLoss1;
    }

    public bool IsRiskFreeReached(Position position, decimal currentPrice)
    {
        if (position.Direction == Direction.Long)
            return currentPrice >= position.RiskFreePrice;

        return currentPrice <= position.RiskFreePrice;
    }
}
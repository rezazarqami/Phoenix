using Phoenix.Core.Entities;

namespace Phoenix.Engine.Managers;

public class EntryManager
{
    public bool CanOpenPosition(Signal signal, decimal currentPrice)
    {
        if (signal.TradePlan == null)
            return false;

        if (signal.Direction == Direction.Long)
            return currentPrice <= signal.TradePlan.EntryPrice;

        return currentPrice >= signal.TradePlan.EntryPrice;
    }
}
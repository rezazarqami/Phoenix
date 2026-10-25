using Phoenix.Core.Entities;

namespace Phoenix.Engine.Services;

public static class QueuedOrderRules
{
    public static bool IsEntryReached(QueuedOrder order, decimal currentPrice)
    {
        if (order.Status != QueuedOrderStatus.Pending || currentPrice <= 0)
            return false;

        return order.Side switch
        {
            "Buy" => currentPrice <= order.EntryPrice,
            "Sell" => currentPrice >= order.EntryPrice,
            _ => throw new InvalidOperationException($"Unsupported queued order side: {order.Side}.")
        };
    }
}

using Phoenix.Core.Entities;

namespace Phoenix.Engine.Managers;

public class ExecutionManager
{
    public Position? OpenPosition(Signal signal)
    {
        if (signal.TradePlan == null)
            return null;

        Position position = new()
        {
            Id = Guid.NewGuid(),

            SignalId = signal.Id,

            Direction = signal.Direction,

            EntryPrice = signal.TradePlan.EntryPrice,

            Quantity = signal.PositionSizeUsdt,

            TakeProfit = signal.TradePlan.TakeProfit,

            StopLoss1 = signal.TradePlan.StopLoss1,

            StopLoss2 = signal.TradePlan.StopLoss2 ?? 0m,

            RiskFreePrice = signal.TradePlan.RiskFreePrice,

            Status = SignalStatus.PositionOpen,

            OpenedAt = DateTime.UtcNow
        };

        signal.Status = SignalStatus.PositionOpen;

        return position;
    }
}
using Phoenix.Core.Entities;

namespace Phoenix.Engine.Managers;

public class ExecutionManager
{
    public Position? OpenPosition(Signal signal)
    {
        var position = PreparePosition(signal);
        if (position is null)
            return null;

        position.Status = SignalStatus.PositionOpen;
        position.OpenedAt = DateTime.UtcNow;
        signal.Status = SignalStatus.PositionOpen;
        return position;
    }

    public Position? PreparePosition(Signal signal)
    {
        if (signal.TradePlan == null || signal.TradePlan.EntryPrice <= 0 ||
            signal.TradePlan.Leverage <= 0 || signal.PositionSizeUsdt <= 0)
            return null;

        Position position = new()
        {
            Id = Guid.NewGuid(),

            SignalId = signal.Id,

            Direction = signal.Direction,

            EntryPrice = signal.TradePlan.EntryPrice,

            Quantity = signal.PositionSizeUsdt * signal.TradePlan.Leverage / signal.TradePlan.EntryPrice,

            PositionSizeUsdt = signal.PositionSizeUsdt,

            TakeProfit = signal.TradePlan.TakeProfit,

            StopLoss1 = signal.TradePlan.StopLoss1,

            StopLoss2 = signal.TradePlan.StopLoss2 ?? 0m,

            RiskFreePrice = signal.TradePlan.RiskFreePrice,

            Status = SignalStatus.WaitingEntry,

            OpenedAt = default
        };
        return position;
    }
}

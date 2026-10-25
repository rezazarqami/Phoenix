using Phoenix.Core.Entities;

namespace Phoenix.Engine.Exchanges;

public interface IExchange
{
    bool SendMarketOrder(Position position);

    bool SendTakeProfit(Position position);

    bool SendStopLoss1(Position position);

    bool SendStopLoss2(Position position);
}
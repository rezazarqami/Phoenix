using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges;

namespace Phoenix.Engine.Managers;

public class OrderManager
{
    private readonly IExchange _exchange;

    public OrderManager(IExchange exchange)
    {
        _exchange = exchange;
    }

    public void PlaceOrders(Position position)
    {
        _exchange.SendMarketOrder(position);

        _exchange.SendTakeProfit(position);

        _exchange.SendStopLoss1(position);

        _exchange.SendStopLoss2(position);
    }
}
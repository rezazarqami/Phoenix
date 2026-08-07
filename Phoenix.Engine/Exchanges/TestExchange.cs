using Phoenix.Core.Entities;

namespace Phoenix.Engine.Exchanges;

public class TestExchange : IExchange
{
    public bool SendMarketOrder(Position position)
    {
        Console.WriteLine("Market Order Sent");

        return true;
    }

    public bool SendTakeProfit(Position position)
    {
        Console.WriteLine("Take Profit Sent");

        return true;
    }

    public bool SendStopLoss1(Position position)
    {
        Console.WriteLine("Stop Loss 1 Sent");

        return true;
    }

    public bool SendStopLoss2(Position position)
    {
        Console.WriteLine("Stop Loss 2 Waiting");

        return true;
    }
}
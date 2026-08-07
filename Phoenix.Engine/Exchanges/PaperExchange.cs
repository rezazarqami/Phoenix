using Phoenix.Core.Entities;

namespace Phoenix.Engine.Exchanges;

public sealed class PaperExchange : IExchange
{
    private readonly List<PaperOrder> _orders = new();

    public IReadOnlyList<PaperOrder> Orders => _orders;

    public bool SendMarketOrder(Position position) => Add("MARKET", position, position.EntryPrice, "FILLED");

    public bool SendTakeProfit(Position position) => Add("TAKE_PROFIT", position, position.TakeProfit, "OPEN");

    public bool SendStopLoss1(Position position) => Add("STOP_LOSS_1", position, position.StopLoss1, "OPEN");

    public bool SendStopLoss2(Position position) => Add("STOP_LOSS_2", position, position.StopLoss2, "WAITING");

    private bool Add(string type, Position position, decimal price, string status)
    {
        if (position.Quantity <= 0 || price <= 0)
            return false;

        _orders.Add(new PaperOrder(DateTime.UtcNow, type, position.Id, price, position.Quantity, status));
        return true;
    }
}

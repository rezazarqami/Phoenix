namespace Phoenix.Core.Entities;

public class Position
{
    public Guid Id { get; set; }

    public Guid SignalId { get; set; }

    public Direction Direction { get; set; }

    public decimal EntryPrice { get; set; }

    public decimal Quantity { get; set; }

    public decimal TakeProfit { get; set; }

    public decimal StopLoss1 { get; set; }

    public decimal StopLoss2 { get; set; }

    public decimal RiskFreePrice { get; set; }

    public SignalStatus Status { get; set; }

    public DateTime OpenedAt { get; set; }
}
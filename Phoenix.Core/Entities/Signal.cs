namespace Phoenix.Core.Entities;

public class Signal
{
    public Guid Id { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public Direction Direction { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal PositionSizeUsdt { get; set; }

    public SignalStatus Status { get; set; }

    public TradePlan? TradePlan { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpireAt { get; set; }
}
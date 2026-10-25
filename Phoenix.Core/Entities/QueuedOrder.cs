namespace Phoenix.Core.Entities;

public enum QueuedOrderStatus
{
    Pending,
    Submitted,
    Paused,
    Error
}

public sealed class QueuedOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal StopLoss { get; set; }
    public decimal PositionSizeUsdt { get; set; }
    public QueuedOrderStatus Status { get; set; } = QueuedOrderStatus.Pending;
    public decimal? LastPrice { get; set; }
    public string? OrderLinkId { get; set; }
    public string? BybitOrderId { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastCheckedAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }

    public DateTime LocalCreatedAt => CreatedAtUtc.ToLocalTime();
    public string DisplayStatus => Status switch
    {
        QueuedOrderStatus.Pending => "در انتظار",
        QueuedOrderStatus.Submitted => "ارسال‌شده",
        QueuedOrderStatus.Paused => "متوقف",
        QueuedOrderStatus.Error => "خطا",
        _ => Status.ToString()
    };
}

namespace Phoenix.Core.Entities;

public class TradePlan
{
    public decimal EntryPrice { get; set; }

    public decimal TakeProfit { get; set; }

    public decimal StopLoss1 { get; set; }

    public decimal Leverage { get; set; }

    public decimal? StopLoss2 { get; set; }

    // نقطه‌ای که با رسیدن قیمت به آن
    // موتور باید SL2 را فعال کند.
    public decimal RiskFreePrice { get; set; }
}

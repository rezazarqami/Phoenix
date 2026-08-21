using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;

namespace Phoenix.Web;

public sealed class SignalCandidateFinder(StrategyCalculator calculator)
{
    public SignalCandidate Find(string symbol, string interval, IReadOnlyList<BybitKline> candles,
        BybitInstrumentRules rules, decimal positionSizeUsdt, int depth = 5)
    {
        if (candles.Count < 60) throw new InvalidOperationException("برای یافتن سیگنال حداقل ۶۰ کندل لازم است.");
        depth = Math.Clamp(depth, 2, 20);
        var highs = new List<(int Index, decimal Price, long Time)>();
        var lows = new List<(int Index, decimal Price, long Time)>();
        for (var i = depth; i < candles.Count - depth; i++)
        {
            if (Enumerable.Range(i - depth, depth * 2 + 1).Where(j => j != i).All(j => candles[i].High > candles[j].High))
                highs.Add((i, candles[i].High, candles[i].OpenTime));
            if (Enumerable.Range(i - depth, depth * 2 + 1).Where(j => j != i).All(j => candles[i].Low < candles[j].Low))
                lows.Add((i, candles[i].Low, candles[i].OpenTime));
        }
        if (highs.Count == 0 || lows.Count == 0) throw new InvalidOperationException("سقف و کف تأییدشده کافی پیدا نشد.");

        var latestPivotIndex = Math.Max(highs[^1].Index, lows[^1].Index);
        var recentHigh = highs.LastOrDefault(x => x.Index >= latestPivotIndex - 80);
        var recentLow = lows.LastOrDefault(x => x.Index >= latestPivotIndex - 80);
        if (recentHigh.Price <= 0 || recentLow.Price <= 0 || recentHigh.Price <= recentLow.Price)
        {
            recentHigh = highs.TakeLast(4).MaxBy(x => x.Price);
            recentLow = lows.TakeLast(4).MinBy(x => x.Price);
        }

        var latest = candles[^1].Close;
        var momentumBase = candles[^Math.Min(21, candles.Count)].Close;
        var direction = latest >= momentumBase ? Direction.Long : Direction.Short;
        var signal = new Signal
        {
            Id = Guid.NewGuid(), Symbol = symbol, Direction = direction,
            High = recentHigh.Price, Low = recentLow.Price, PositionSizeUsdt = positionSizeUsdt,
            CreatedAt = DateTime.UtcNow, Status = SignalStatus.WaitingEntry
        };
        signal.TradePlan = calculator.Calculate(signal);
        signal.TradePlan.Leverage = BybitLeverageRules.Normalize(signal.TradePlan.Leverage, rules);
        var position = new ExecutionManager().PreparePosition(signal)
            ?? throw new InvalidOperationException("پیش‌نمایش موقعیت ساخته نشد.");
        var preview = BybitOrderPreviewBuilder.Build(symbol, position, rules);

        var rangePercent = (recentHigh.Price - recentLow.Price) / recentLow.Price * 100m;
        var momentumPercent = Math.Abs(latest - momentumBase) / momentumBase * 100m;
        var freshness = candles.Count - 1 - Math.Max(recentHigh.Index, recentLow.Index);
        var confidence = Math.Clamp(55m + Math.Min(momentumPercent * 4m, 18m) +
            Math.Min(rangePercent, 12m) - Math.Min(freshness * 0.35m, 18m), 35m, 92m);

        return new SignalCandidate(symbol, interval, direction.ToString(), recentHigh.Price, recentLow.Price,
            latest, preview.Price, preview.TakeProfit, preview.StopLoss, signal.TradePlan.StopLoss2,
            signal.TradePlan.RiskFreePrice, signal.TradePlan.Leverage, preview.Quantity,
            Math.Round(confidence, 1), recentHigh.Time, recentLow.Time,
            direction == Direction.Long
                ? "مومنتوم ۲۰ کندل اخیر صعودی است؛ ورود روی اصلاح محدوده پیشنهاد شده است."
                : "مومنتوم ۲۰ کندل اخیر نزولی است؛ ورود روی بازگشت محدوده پیشنهاد شده است.");
    }
}

public sealed record SignalCandidate(string Symbol, string Interval, string Direction, decimal Ceiling, decimal Floor,
    decimal LastPrice, decimal EntryPrice, decimal TakeProfit, decimal StopLoss, decimal? StopLoss2,
    decimal? RiskFreePrice, decimal Leverage, decimal Quantity, decimal Confidence,
    long CeilingTime, long FloorTime, string Rationale);

using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;

namespace Phoenix.Web;

public sealed class SignalCandidateFinder(StrategyCalculator calculator)
{
    public SignalCandidate Find(string symbol, string interval, IReadOnlyList<BybitKline> candles,
        BybitInstrumentRules rules, decimal positionSizeUsdt, int depth = 5, bool useClosePrices = false)
    {
        if (candles.Count < 30) throw new InvalidOperationException("برای یافتن سقف و کف ماژور، حداقل ۳۰ کندل را روی نمودار نگه دارید.");
        depth = Math.Clamp(depth, 2, 20);
        var highs = new List<(int Index, decimal Price, long Time)>();
        var lows = new List<(int Index, decimal Price, long Time)>();
        decimal HighAt(int index) => useClosePrices ? candles[index].Close : candles[index].High;
        decimal LowAt(int index) => useClosePrices ? candles[index].Close : candles[index].Low;
        for (var i = depth; i < candles.Count - depth; i++)
        {
            if (Enumerable.Range(i - depth, depth * 2 + 1).Where(j => j != i).All(j => HighAt(i) > HighAt(j)))
                highs.Add((i, HighAt(i), candles[i].OpenTime));
            if (Enumerable.Range(i - depth, depth * 2 + 1).Where(j => j != i).All(j => LowAt(i) < LowAt(j)))
                lows.Add((i, LowAt(i), candles[i].OpenTime));
        }
        if (highs.Count == 0 || lows.Count == 0) throw new InvalidOperationException("سقف و کف تأییدشده کافی پیدا نشد.");

        // The visible chart window defines context. Pick the most prominent
        // confirmed high and low inside that window as its major anchors.
        var recentHigh = highs.MaxBy(x => x.Price);
        var recentLow = lows.MinBy(x => x.Price);
        var resetCount = 0;

        if (recentLow.Time < recentHigh.Time)
        {
            var activeLow = recentLow;
            var risingHighs = highs.Where(x => x.Index > activeLow.Index && x.Index <= recentHigh.Index)
                .OrderBy(x => x.Index).ToArray();
            for (var i = 0; i < risingHighs.Length - 1; i++)
            {
                var peak = risingHighs[i];
                var nextHigher = risingHighs.Skip(i + 1).FirstOrDefault(x => x.Price > peak.Price);
                if (nextHigher == default || peak.Price <= activeLow.Price) continue;
                var entry = StrategyCalculator.LogarithmicLevel(activeLow.Price, peak.Price, 1m - 0.618m);
                var touched = Enumerable.Range(peak.Index + 1, nextHigher.Index - peak.Index - 1)
                    .Any(index => LowAt(index) <= entry);
                if (!touched) continue;
                var nextLow = lows.Where(x => x.Index > peak.Index && x.Index < nextHigher.Index)
                    .MinBy(x => x.Price);
                if (nextLow == default) continue;
                activeLow = nextLow;
                resetCount++;
            }
            recentLow = activeLow;
        }
        else
        {
            var activeHigh = recentHigh;
            var fallingLows = lows.Where(x => x.Index > activeHigh.Index && x.Index <= recentLow.Index)
                .OrderBy(x => x.Index).ToArray();
            for (var i = 0; i < fallingLows.Length - 1; i++)
            {
                var trough = fallingLows[i];
                var nextLower = fallingLows.Skip(i + 1).FirstOrDefault(x => x.Price < trough.Price);
                if (nextLower == default || activeHigh.Price <= trough.Price) continue;
                var entry = StrategyCalculator.LogarithmicLevel(trough.Price, activeHigh.Price, 0.618m);
                var touched = Enumerable.Range(trough.Index + 1, nextLower.Index - trough.Index - 1)
                    .Any(index => HighAt(index) >= entry);
                if (!touched) continue;
                var nextHigh = highs.Where(x => x.Index > trough.Index && x.Index < nextLower.Index)
                    .MaxBy(x => x.Price);
                if (nextHigh == default) continue;
                activeHigh = nextHigh;
                resetCount++;
            }
            recentHigh = activeHigh;
        }

        var latest = candles[^1].Close;
        var momentumBase = candles[^Math.Min(21, candles.Count)].Close;
        // Direction follows the chronological move between the selected major
        // anchors: low then high is an upward range; high then low is downward.
        var direction = recentLow.Time < recentHigh.Time ? Direction.Long : Direction.Short;
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
        var formedAtIndex = Math.Max(recentHigh.Index, recentLow.Index);
        var touchedCandle = candles.Skip(formedAtIndex + 1).Select((candle, offset) => new
            {
                Candle = candle,
                Index = formedAtIndex + 1 + offset
            })
            .FirstOrDefault(item => direction == Direction.Long
                ? LowAt(item.Index) <= preview.Price
                : HighAt(item.Index) >= preview.Price);
        var isBurned = touchedCandle is not null;

        var rangePercent = (recentHigh.Price - recentLow.Price) / recentLow.Price * 100m;
        var momentumPercent = Math.Abs(latest - momentumBase) / momentumBase * 100m;
        var freshness = candles.Count - 1 - Math.Max(recentHigh.Index, recentLow.Index);
        var confidence = Math.Clamp(55m + Math.Min(momentumPercent * 4m, 18m) +
            Math.Min(rangePercent, 12m) - Math.Min(freshness * 0.35m, 18m), 35m, 92m);

        return new SignalCandidate(symbol, interval, direction.ToString(), recentHigh.Price, recentLow.Price,
            latest, preview.Price, preview.TakeProfit, preview.StopLoss, signal.TradePlan.StopLoss2,
            signal.TradePlan.RiskFreePrice, signal.TradePlan.Leverage, preview.Quantity,
            Math.Round(confidence, 1), recentHigh.Time, recentLow.Time,
            candles[0].OpenTime, candles[^1].OpenTime, candles.Count,
            (direction == Direction.Long
                ? $"کف پیش از سقف تشکیل شده است؛ {resetCount} اصلاح کامل ۶۱٫۸٪ شناسایی و کف فعال به‌روزرسانی شد. پیشنهاد Long است."
                : $"سقف پیش از کف تشکیل شده است؛ {resetCount} اصلاح کامل ۶۱٫۸٪ شناسایی و سقف فعال به‌روزرسانی شد. پیشنهاد Short است.") +
            (isBurned ? " نقطه ورود پس از تشکیل محدوده لمس شده و این سیگنال سوخته است." : " نقطه ورود هنوز لمس نشده و سیگنال فعال است."),
            isBurned, touchedCandle?.Candle.OpenTime);
    }
}

public sealed record SignalCandidate(string Symbol, string Interval, string Direction, decimal Ceiling, decimal Floor,
    decimal LastPrice, decimal EntryPrice, decimal TakeProfit, decimal StopLoss, decimal? StopLoss2,
    decimal? RiskFreePrice, decimal Leverage, decimal Quantity, decimal Confidence,
    long CeilingTime, long FloorTime, long RangeStartTime, long RangeEndTime, int RangeCandleCount, string Rationale,
    bool IsBurned, long? EntryTouchedTime);

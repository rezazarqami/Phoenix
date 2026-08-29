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
        if (highs.Count == 0 || lows.Count == 0)
            throw new InvalidOperationException("سقف و کف تأییدشده کافی پیدا نشد.");

        // First determine the move from its confirmed major anchors. Only the
        // second anchor is then extended to the true visible-range extreme:
        // the maximum for Long and the minimum for Short. The first anchor is
        // kept on the 61.8%-cycle logic below so it represents the beginning
        // of the latest uninterrupted impulse, not merely a range extreme.
        var points = Enumerable.Range(0, candles.Count)
            .Select(index => (Index: index, High: HighAt(index), Low: LowAt(index), Time: candles[index].OpenTime))
            .ToArray();
        var recentHigh = highs.MaxBy(x => x.Price);
        var recentLow = lows.MinBy(x => x.Price);
        var initialDirection = recentLow.Time < recentHigh.Time ? Direction.Long : Direction.Short;
        if (initialDirection == Direction.Long)
        {
            var highPoint = points.Where(x => x.Index > recentLow.Index).MaxBy(x => x.High);
            recentHigh = (highPoint.Index, Price: highPoint.High, highPoint.Time);
        }
        else
        {
            var lowPoint = points.Where(x => x.Index > recentHigh.Index).MinBy(x => x.Low);
            recentLow = (lowPoint.Index, Price: lowPoint.Low, lowPoint.Time);
        }
        if (recentHigh.Price <= recentLow.Price)
            throw new InvalidOperationException("دامنه قیمت کافی برای ساخت سیگنال پیدا نشد.");
        var resetCount = 0;

        if (recentLow.Time < recentHigh.Time)
        {
            var activeLow = recentLow;
            var peakPrice = activeLow.Price;
            var correctionTouched = false;
            var correctionLow = activeLow;
            for (var index = activeLow.Index + 1; index <= recentHigh.Index; index++)
            {
                var high = HighAt(index);
                var low = LowAt(index);
                if (!correctionTouched)
                {
                    if (high > peakPrice) peakPrice = high;
                    if (peakPrice <= activeLow.Price) continue;
                    var retracement = StrategyCalculator.LogarithmicLevel(
                        activeLow.Price, peakPrice, 1m - 0.618m);
                    if (low > retracement) continue;
                    correctionTouched = true;
                    correctionLow = (index, low, candles[index].OpenTime);
                    continue;
                }

                if (low < correctionLow.Price)
                    correctionLow = (index, low, candles[index].OpenTime);
                if (high <= peakPrice) continue;

                activeLow = correctionLow;
                peakPrice = high;
                correctionTouched = false;
                resetCount++;
            }
            recentLow = activeLow;
        }
        else
        {
            var activeHigh = recentHigh;
            var troughPrice = activeHigh.Price;
            var correctionTouched = false;
            var correctionHigh = activeHigh;
            for (var index = activeHigh.Index + 1; index <= recentLow.Index; index++)
            {
                var high = HighAt(index);
                var low = LowAt(index);
                if (!correctionTouched)
                {
                    if (low < troughPrice) troughPrice = low;
                    if (activeHigh.Price <= troughPrice) continue;
                    var retracement = StrategyCalculator.LogarithmicLevel(
                        troughPrice, activeHigh.Price, 0.618m);
                    if (high < retracement) continue;
                    correctionTouched = true;
                    correctionHigh = (index, high, candles[index].OpenTime);
                    continue;
                }

                if (high > correctionHigh.Price)
                    correctionHigh = (index, high, candles[index].OpenTime);
                if (low >= troughPrice) continue;

                activeHigh = correctionHigh;
                troughPrice = low;
                correctionTouched = false;
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
                ? $"کف پیش از سقف تشکیل شده است؛ {resetCount} اصلاح کامل ۶۱٫۸٪ شناسایی و کف فعال مرحله‌به‌مرحله به‌روزرسانی شد. پیشنهاد Long است."
                : $"سقف پیش از کف تشکیل شده است؛ {resetCount} اصلاح کامل ۶۱٫۸٪ شناسایی و سقف فعال مرحله‌به‌مرحله به‌روزرسانی شد. پیشنهاد Short است.") +
            (isBurned ? " نقطه ورود پس از تشکیل محدوده لمس شده و این سیگنال سوخته است." : " نقطه ورود هنوز لمس نشده و سیگنال فعال است."),
            isBurned, touchedCandle?.Candle.OpenTime);
    }
}

public sealed record SignalCandidate(string Symbol, string Interval, string Direction, decimal Ceiling, decimal Floor,
    decimal LastPrice, decimal EntryPrice, decimal TakeProfit, decimal StopLoss, decimal? StopLoss2,
    decimal? RiskFreePrice, decimal Leverage, decimal Quantity, decimal Confidence,
    long CeilingTime, long FloorTime, long RangeStartTime, long RangeEndTime, int RangeCandleCount, string Rationale,
    bool IsBurned, long? EntryTouchedTime);

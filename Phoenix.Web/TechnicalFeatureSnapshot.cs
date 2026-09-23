using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed record TechnicalFeatureSnapshot(
    decimal Rsi14,
    decimal AtrPercent,
    decimal TrendStrength,
    decimal IchimokuPosition,
    decimal IchimokuCloudBias,
    decimal SupportDistanceAtr,
    decimal ResistanceDistanceAtr,
    decimal FibonacciAlignment,
    decimal PriceActionScore,
    decimal VolumeRatio,
    decimal ImpulseEfficiency,
    decimal HigherTimeframeAlignment = 0m,
    decimal BitcoinMarketAlignment = 0m,
    decimal MarketRegimeScore = 0m,
    decimal StructureScore = 0m,
    decimal LiquiditySweepScore = 0m,
    decimal? EntryLevelStrength = null,
    decimal? StopLevelStrength = null,
    decimal? TargetLevelStrength = null,
    decimal? IchimokuEntryPosition = null,
    decimal? IchimokuStopPosition = null,
    decimal? IchimokuTargetPosition = null);

public static class TechnicalFeatureExtractor
{
    public static TechnicalFeatureSnapshot Calculate(IReadOnlyList<BybitKline> source,
        SignalCandidate candidate)
    {
        var candles = source.Count > 240 ? source.Skip(source.Count - 240).ToArray() : source.ToArray();
        if (candles.Length < 52) throw new InvalidOperationException("حداقل ۵۲ کندل برای تحلیل فنی لازم است.");
        var closes = candles.Select(x => x.Close).ToArray();
        var atr = Atr(candles, 14);
        var basis = Math.Max(closes[^1], 0.00000001m);
        var ema20 = Ema(closes, 20);
        var ema50 = Ema(closes, 50);
        var trend = atr <= 0m ? 0m : Clamp((ema20 - ema50) / atr / 4m, -1m, 1m);

        var conversion = Midpoint(candles, 9);
        var baseLine = Midpoint(candles, 26);
        var spanA = (conversion + baseLine) / 2m;
        var spanB = Midpoint(candles, 52);
        var cloudTop = Math.Max(spanA, spanB);
        var cloudBottom = Math.Min(spanA, spanB);
        var cloudPosition = atr <= 0m ? 0m : Clamp((closes[^1] - (cloudTop + cloudBottom) / 2m) / atr / 3m, -1m, 1m);
        var cloudBias = atr <= 0m ? 0m : Clamp((spanA - spanB) / atr / 2m, -1m, 1m);

        var lookback = candles.Skip(Math.Max(0, candles.Length - 150)).ToArray();
        var swingLows = Swings(lookback, false);
        var swingHighs = Swings(lookback, true);
        var support = swingLows.Where(x => x <= candidate.EntryPrice).DefaultIfEmpty(lookback.Min(x => x.Low)).Max();
        var resistance = swingHighs.Where(x => x >= candidate.EntryPrice).DefaultIfEmpty(lookback.Max(x => x.High)).Min();
        var supportDistance = atr <= 0m ? 10m : Clamp(Math.Abs(candidate.EntryPrice - support) / atr, 0m, 10m);
        var resistanceDistance = atr <= 0m ? 10m : Clamp(Math.Abs(resistance - candidate.EntryPrice) / atr, 0m, 10m);
        var swings = swingLows.Concat(swingHighs).ToArray();
        // Confirmed pivots within half an ATR measure repeated tests of each
        // actual trade level; absent historical fields remain missing, not zero.
        decimal Strength(decimal level) => atr <= 0m ? 0m :
            Clamp(swings.Count(x => Math.Abs(x - level) <= atr * 0.5m) / 4m, 0m, 1m);
        decimal CloudAt(decimal level) => atr <= 0m ? 0m :
            Clamp((level - (cloudTop + cloudBottom) / 2m) / atr / 3m, -1m, 1m);

        var range = Math.Max(candidate.Ceiling - candidate.Floor, 0.00000001m);
        var entryPosition = (candidate.EntryPrice - candidate.Floor) / range;
        var fibDistance = new[] { 0.382m, 0.5m, 0.618m, 0.786m }
            .Min(level => Math.Abs(entryPosition - level));
        var fibAlignment = Clamp(1m - fibDistance / 0.15m, 0m, 1m);

        var recent = candles.Skip(candles.Length - Math.Min(8, candles.Length)).ToArray();
        var directionalBars = recent.Select(candle =>
        {
            var candleRange = Math.Max(candle.High - candle.Low, 0.00000001m);
            var closeLocation = (candle.Close - candle.Low) / candleRange * 2m - 1m;
            var body = Math.Abs(candle.Close - candle.Open) / candleRange;
            return closeLocation * (0.45m + body * 0.55m);
        }).Average();
        var direction = candidate.Direction == "Long" ? 1m : -1m;
        var priceAction = Clamp((directionalBars * direction + 1m) / 2m, 0m, 1m);

        var recentVolume = candles.Skip(candles.Length - 10).Average(x => x.Volume);
        var baseVolume = candles.Skip(Math.Max(0, candles.Length - 50)).Average(x => x.Volume);
        var volumeRatio = baseVolume <= 0m ? 1m : Clamp(recentVolume / baseVolume, 0m, 3m);

        var structure = StructureScore(swingHighs, swingLows, direction);
        var liquiditySweep = LiquiditySweep(candles, direction);

        return new(
            Rsi(candles, 14) / 100m,
            Clamp(atr / basis * 100m, 0m, 20m),
            trend,
            cloudPosition,
            cloudBias,
            supportDistance,
            resistanceDistance,
            fibAlignment,
            priceAction,
            volumeRatio,
            Clamp(SignalQualityAssessment.ImpulseEfficiency(candidate, candles), 0m, 1m),
            StructureScore: structure,
            LiquiditySweepScore: liquiditySweep,
            EntryLevelStrength: Strength(candidate.EntryPrice),
            StopLevelStrength: Strength(candidate.StopLoss),
            TargetLevelStrength: Strength(candidate.TakeProfit),
            IchimokuEntryPosition: CloudAt(candidate.EntryPrice),
            IchimokuStopPosition: CloudAt(candidate.StopLoss),
            IchimokuTargetPosition: CloudAt(candidate.TakeProfit));
    }

    private static decimal[] Swings(IReadOnlyList<BybitKline> candles, bool highs)
    {
        var result = new List<decimal>();
        for (var i = 2; i < candles.Count - 2; i++)
        {
            var value = highs ? candles[i].High : candles[i].Low;
            var swing = highs
                ? value >= candles[i - 1].High && value >= candles[i - 2].High && value >= candles[i + 1].High && value >= candles[i + 2].High
                : value <= candles[i - 1].Low && value <= candles[i - 2].Low && value <= candles[i + 1].Low && value <= candles[i + 2].Low;
            if (swing) result.Add(value);
        }
        return result.ToArray();
    }

    private static decimal StructureScore(IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, decimal direction)
    {
        if (highs.Count < 2 || lows.Count < 2) return 0.5m;
        var bullish = highs[^1] > highs[^2] && lows[^1] > lows[^2];
        var bearish = highs[^1] < highs[^2] && lows[^1] < lows[^2];
        var aligned = direction > 0m ? bullish : bearish;
        var opposed = direction > 0m ? bearish : bullish;
        return aligned ? 1m : opposed ? 0m : 0.5m;
    }

    private static decimal LiquiditySweep(IReadOnlyList<BybitKline> candles, decimal direction)
    {
        if (candles.Count < 25) return 0.5m;
        var last = candles[^1];
        var prior = candles.Skip(candles.Count - 22).Take(20).ToArray();
        var sweptLow = last.Low < prior.Min(x => x.Low) && last.Close > prior.Min(x => x.Low);
        var sweptHigh = last.High > prior.Max(x => x.High) && last.Close < prior.Max(x => x.High);
        return direction > 0m ? (sweptLow ? 1m : sweptHigh ? 0m : 0.5m)
            : sweptHigh ? 1m : sweptLow ? 0m : 0.5m;
    }

    private static decimal Midpoint(IReadOnlyList<BybitKline> candles, int period)
    {
        var range = candles.Skip(Math.Max(0, candles.Count - period));
        return (range.Max(x => x.High) + range.Min(x => x.Low)) / 2m;
    }

    private static decimal Atr(IReadOnlyList<BybitKline> candles, int period)
    {
        var values = new List<decimal>();
        for (var i = Math.Max(1, candles.Count - period); i < candles.Count; i++)
            values.Add(Math.Max(candles[i].High - candles[i].Low,
                Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close),
                    Math.Abs(candles[i].Low - candles[i - 1].Close))));
        return values.Count == 0 ? 0m : values.Average();
    }

    private static decimal Rsi(IReadOnlyList<BybitKline> candles, int period)
    {
        decimal gain = 0m, loss = 0m;
        for (var i = Math.Max(1, candles.Count - period); i < candles.Count; i++)
        {
            var change = candles[i].Close - candles[i - 1].Close;
            if (change > 0m) gain += change; else loss -= change;
        }
        if (loss == 0m) return gain == 0m ? 50m : 100m;
        return 100m - 100m / (1m + gain / loss);
    }

    private static decimal Ema(IReadOnlyList<decimal> values, int period)
    {
        var factor = 2m / (period + 1m);
        var ema = values[0];
        foreach (var value in values.Skip(1)) ema = value * factor + ema * (1m - factor);
        return ema;
    }

    private static decimal Clamp(decimal value, decimal min, decimal max) => Math.Min(max, Math.Max(min, value));
}

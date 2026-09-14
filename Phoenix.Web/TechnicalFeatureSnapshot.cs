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
    decimal ImpulseEfficiency);

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

        var lookback = candles.Skip(Math.Max(0, candles.Length - 100)).ToArray();
        var support = lookback.Min(x => x.Low);
        var resistance = lookback.Max(x => x.High);
        var supportDistance = atr <= 0m ? 10m : Clamp(Math.Abs(candidate.EntryPrice - support) / atr, 0m, 10m);
        var resistanceDistance = atr <= 0m ? 10m : Clamp(Math.Abs(resistance - candidate.EntryPrice) / atr, 0m, 10m);

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
            Clamp(SignalQualityAssessment.ImpulseEfficiency(candidate, candles), 0m, 1m));
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

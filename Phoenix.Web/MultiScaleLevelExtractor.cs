using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

// Persist the proposal-time readings. Recomputing an old signal from today's
// candles would leak future price moves into its historical outcome example.
public sealed record MultiScaleLevelSnapshot(string Interval, int Window,
    decimal FibonacciEntry, decimal FibonacciStop, decimal FibonacciTarget,
    decimal EntryStrength, decimal StopStrength, decimal TargetStrength,
    decimal CloudEntry, decimal CloudStop, decimal CloudTarget, decimal CloudBias);

public static class MultiScaleLevelExtractor
{
    private static readonly int[] Windows = [80, 240, 720];
    private static readonly decimal[] Retracements = [0.236m, 0.382m, 0.5m, 0.618m, 0.786m];

    public static IReadOnlyList<MultiScaleLevelSnapshot> Calculate(
        IReadOnlyDictionary<string, IReadOnlyList<BybitKline>> intervals, SignalCandidate candidate)
    {
        var result = new List<MultiScaleLevelSnapshot>();
        foreach (var (interval, history) in intervals)
        foreach (var window in Windows)
        {
            if (history.Count < window) continue;
            var candles = history.Skip(history.Count - window).ToArray();
            var high = candles.Max(x => x.High);
            var low = candles.Min(x => x.Low);
            var range = high - low;
            if (range <= 0m) continue;
            var features = TechnicalFeatureExtractor.Calculate(candles, candidate);
            var atr = features.AtrPercent * Math.Max(candles[^1].Close, 0.00000001m) / 100m;
            decimal Fib(decimal level)
            {
                var distance = Retracements.Min(ratio =>
                    Math.Min(Math.Abs(level - (low + range * ratio)),
                        Math.Abs(level - (high - range * ratio))));
                // An ATR-sized neighborhood keeps a large remote swing from
                // declaring unrelated prices to be perfectly aligned.
                return Math.Clamp(1m - distance / Math.Max(atr, range * 0.012m), 0m, 1m);
            }
            result.Add(new(interval, window,
                Fib(candidate.EntryPrice), Fib(candidate.StopLoss), Fib(candidate.TakeProfit),
                features.EntryLevelStrength ?? 0m, features.StopLevelStrength ?? 0m,
                features.TargetLevelStrength ?? 0m,
                features.IchimokuEntryPosition ?? 0m, features.IchimokuStopPosition ?? 0m,
                features.IchimokuTargetPosition ?? 0m, features.IchimokuCloudBias));
        }
        return result;
    }
}

using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class ElliottWaveAnalyzer
{
    public ElliottAnalysis Analyze(IReadOnlyList<BybitKline> candles, int depth = 5, decimal deviationPercent = 0.6m)
    {
        if (candles.Count < 30)
            return new ElliottAnalysis([], [], "برای تحلیل حداقل ۳۰ کندل لازم است.");
        depth = Math.Clamp(depth, 2, 20);
        deviationPercent = Math.Clamp(deviationPercent, 0.05m, 20m);
        var pivots = FindPivots(candles, depth, deviationPercent);
        var candidates = new List<ElliottScenario>();
        for (var start = Math.Max(0, pivots.Count - 18); start + 5 < pivots.Count; start++)
        {
            var points = pivots.Skip(start).Take(6).ToArray();
            var candidate = ScoreImpulse(points);
            if (candidate is not null) candidates.Add(candidate);
        }
        var ranked = candidates.OrderByDescending(x => x.Score).Take(3).ToArray();
        var message = ranked.Length == 0
            ? "ساختار پنج‌موجی معتبر پیدا نشد؛ پیوت‌های مهم برای بررسی دستی نمایش داده شده‌اند."
            : "سناریوها بر اساس قوانین سخت الیوت، تناوب پیوت‌ها و نسبت‌های فیبوناچی رتبه‌بندی شده‌اند.";
        return new ElliottAnalysis(pivots, ranked, message);
    }

    private static List<ElliottPivot> FindPivots(IReadOnlyList<BybitKline> candles, int depth, decimal deviationPercent)
    {
        var raw = new List<ElliottPivot>();
        for (var i = depth; i < candles.Count - depth; i++)
        {
            var high = candles[i].High;
            var low = candles[i].Low;
            var isHigh = true;
            var isLow = true;
            for (var j = i - depth; j <= i + depth; j++)
            {
                if (j == i) continue;
                if (candles[j].High >= high) isHigh = false;
                if (candles[j].Low <= low) isLow = false;
            }
            if (isHigh) AddOrReplace(raw, new ElliottPivot(i, candles[i].OpenTime, high, "High"), deviationPercent);
            if (isLow) AddOrReplace(raw, new ElliottPivot(i, candles[i].OpenTime, low, "Low"), deviationPercent);
        }
        return raw;
    }

    private static void AddOrReplace(List<ElliottPivot> pivots, ElliottPivot next, decimal deviationPercent)
    {
        if (pivots.Count == 0) { pivots.Add(next); return; }
        var previous = pivots[^1];
        if (previous.Kind == next.Kind)
        {
            var moreExtreme = next.Kind == "High" ? next.Price > previous.Price : next.Price < previous.Price;
            if (moreExtreme) pivots[^1] = next;
            return;
        }
        var move = Math.Abs(next.Price - previous.Price) / Math.Max(previous.Price, 0.00000001m) * 100m;
        if (move >= deviationPercent) pivots.Add(next);
    }

    private static ElliottScenario? ScoreImpulse(IReadOnlyList<ElliottPivot> p)
    {
        if (p.Count != 6) return null;
        var bullish = p[0].Kind == "Low";
        for (var i = 0; i < p.Count; i++)
        {
            var expected = bullish ? (i % 2 == 0 ? "Low" : "High") : (i % 2 == 0 ? "High" : "Low");
            if (p[i].Kind != expected) return null;
        }
        var w1 = Math.Abs(p[1].Price - p[0].Price);
        var w3 = Math.Abs(p[3].Price - p[2].Price);
        var w5 = Math.Abs(p[5].Price - p[4].Price);
        if (w1 == 0 || w3 == 0 || w5 == 0) return null;

        var wave2Valid = bullish ? p[2].Price > p[0].Price : p[2].Price < p[0].Price;
        var wave3Extreme = bullish ? p[3].Price > p[1].Price : p[3].Price < p[1].Price;
        var wave3NotShortest = w3 >= Math.Min(w1, w5);
        var wave4NoOverlap = bullish ? p[4].Price > p[1].Price : p[4].Price < p[1].Price;
        var wave5Extreme = bullish ? p[5].Price > p[3].Price : p[5].Price < p[3].Price;
        if (!wave2Valid || !wave3Extreme || !wave3NotShortest) return null;

        var score = 55m;
        if (wave4NoOverlap) score += 15m;
        if (wave5Extreme) score += 10m;
        var retrace2 = Math.Abs(p[2].Price - p[1].Price) / w1;
        var extension3 = w3 / w1;
        var retrace4 = Math.Abs(p[4].Price - p[3].Price) / w3;
        score += FibScore(retrace2, 0.5m, 0.618m) * 8m;
        score += FibScore(extension3, 1.0m, 1.618m) * 7m;
        score += FibScore(retrace4, 0.236m, 0.382m) * 5m;
        score = Math.Round(Math.Min(score, 100m), 1);

        var rules = new[]
        {
            new ElliottRule("wave2", "موج ۲ ابتدای موج ۱ را نقض نکرده است.", wave2Valid),
            new ElliottRule("wave3", "موج ۳ کوتاه‌ترین موج جنبشی نیست.", wave3NotShortest),
            new ElliottRule("wave4", "موج ۴ با محدوده موج ۱ هم‌پوشانی ندارد.", wave4NoOverlap),
            new ElliottRule("wave5", "موج ۵ از انتهای موج ۳ عبور کرده است.", wave5Extreme)
        };
        var labels = p.Select((point, i) => new ElliottWavePoint(i == 0 ? "0" : i.ToString(), point.Time, point.Price)).ToArray();
        return new ElliottScenario(
            bullish ? "Bullish" : "Bearish", score, labels, rules,
            p[0].Price, bullish ? p[4].Price : p[4].Price,
            new ElliottRatios(Math.Round(retrace2, 3), Math.Round(extension3, 3), Math.Round(retrace4, 3)));
    }

    private static decimal FibScore(decimal value, params decimal[] targets)
    {
        var distance = targets.Min(target => Math.Abs(value - target) / Math.Max(target, 0.0001m));
        return Math.Max(0m, 1m - distance);
    }
}

public sealed record ElliottAnalysis(IReadOnlyList<ElliottPivot> Pivots, IReadOnlyList<ElliottScenario> Scenarios, string Message);
public sealed record ElliottPivot(int Index, long Time, decimal Price, string Kind);
public sealed record ElliottScenario(string Direction, decimal Score, IReadOnlyList<ElliottWavePoint> Waves,
    IReadOnlyList<ElliottRule> Rules, decimal StartInvalidation, decimal Wave4Invalidation, ElliottRatios Ratios);
public sealed record ElliottWavePoint(string Label, long Time, decimal Price);
public sealed record ElliottRule(string Code, string Description, bool Passed);
public sealed record ElliottRatios(decimal Wave2Retracement, decimal Wave3Extension, decimal Wave4Retracement);

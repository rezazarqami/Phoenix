using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

/// <summary>Hard Elliott rules invalidate counts; ratios and alternation only rank them.</summary>
public sealed class ElliottWaveAnalyzer
{
    public const string RuleSetVersion = "2.0";

    public ElliottAnalysis Analyze(IReadOnlyList<BybitKline> candles, int depth = 5, decimal deviationPercent = 0.6m)
    {
        if (candles.Count < 30) return new([], [], "برای تحلیل حداقل ۳۰ کندل لازم است.", RuleSetVersion);
        var pivots = FindPivots(candles, Math.Clamp(depth, 2, 20), Math.Clamp(deviationPercent, .05m, 20m));
        var found = new List<ElliottScenario>();
        void Add(ElliottScenario? value) { if (value is not null) found.Add(value); }
        for (var start = Math.Max(0, pivots.Count - 24); start < pivots.Count; start++)
        {
            var left = pivots.Count - start;
            if (left >= 6)
            {
                var six = pivots.Skip(start).Take(6).ToArray(); Add(ScoreImpulse(six)); Add(ScoreTriangle(six));
            }
            if (left >= 4) Add(ScoreCorrection(pivots.Skip(start).Take(4).ToArray()));
        }
        foreach (var count in new[] { 5, 4, 3 })
            if (pivots.Count >= count) Add(ScoreDeveloping(pivots.TakeLast(count).ToArray()));
        var ranked = found.GroupBy(x => $"{x.Pattern}|{string.Join(',', x.Waves.Select(w => w.Time))}")
            .Select(x => x.MaxBy(y => y.Score)!)
            .OrderByDescending(x => x.Score - 4m * pivots.Count(p => p.Time > x.Waves[^1].Time))
            .ThenByDescending(x => x.Waves[^1].Time).Take(5).ToArray();
        var message = ranked.Length == 0
            ? "ساختار معتبر پیدا نشد؛ پیوت‌های مهم برای بررسی دستی نمایش داده شده‌اند."
            : "سناریوها با جداسازی قوانین سخت از راهنماهای فیبوناچی و تناوب رتبه‌بندی شده‌اند؛ سناریوی جایگزین را نیز بررسی کنید.";
        return new(pivots, ranked, message, RuleSetVersion);
    }

    private static List<ElliottPivot> FindPivots(IReadOnlyList<BybitKline> candles, int depth, decimal deviation)
    {
        var result = new List<ElliottPivot>();
        for (var i = depth; i < candles.Count - depth; i++)
        {
            var high = Enumerable.Range(i - depth, depth * 2 + 1).Where(j => j != i).All(j => candles[i].High > candles[j].High);
            var low = Enumerable.Range(i - depth, depth * 2 + 1).Where(j => j != i).All(j => candles[i].Low < candles[j].Low);
            if (high) AddPivot(result, new(i, candles[i].OpenTime, candles[i].High, "High"), deviation);
            if (low) AddPivot(result, new(i, candles[i].OpenTime, candles[i].Low, "Low"), deviation);
        }
        return result;
    }

    private static void AddPivot(List<ElliottPivot> pivots, ElliottPivot next, decimal deviation)
    {
        if (pivots.Count == 0) { pivots.Add(next); return; }
        var previous = pivots[^1];
        if (previous.Kind == next.Kind)
        {
            if (next.Kind == "High" ? next.Price > previous.Price : next.Price < previous.Price) pivots[^1] = next;
            return;
        }
        if (Math.Abs(next.Price - previous.Price) / Math.Max(previous.Price, .00000001m) * 100m >= deviation) pivots.Add(next);
    }

    private static ElliottScenario? ScoreImpulse(IReadOnlyList<ElliottPivot> p)
    {
        if (!Alternates(p)) return null;
        var bull = p[0].Kind == "Low"; var w1 = Leg(p, 0); var w3 = Leg(p, 2); var w5 = Leg(p, 4);
        var wave2 = Beyond(p[2].Price, p[0].Price, bull);
        var wave3Extreme = Beyond(p[3].Price, p[1].Price, bull);
        var wave3 = w3 >= Math.Min(w1, w5);
        if (!wave2 || !wave3Extreme || !wave3 || w1 == 0) return null;
        var overlap = !Beyond(p[4].Price, p[1].Price, bull);
        var diagonal = overlap && w3 < w1 && w5 < w3;
        if (overlap && !diagonal) return null;
        var truncated = !Beyond(p[5].Price, p[3].Price, bull);
        var r2 = Leg(p, 1) / w1; var e3 = w3 / w1; var r4 = Leg(p, 3) / w3;
        var alternating = (r2 >= .5m && r4 <= .5m) || (r2 <= .5m && r4 >= .5m);
        var score = 58m + Fib(r2, .5m, .618m, .786m) * 10 + Fib(e3, 1m, 1.618m, 2.618m) * 10
            + Fib(r4, .236m, .382m, .5m) * 8 + (alternating ? 6 : 0) + (truncated ? -7 : 3);
        var rules = new ElliottRule[]
        {
            Hard("wave2-origin", "موج ۲ از مبدأ موج ۱ عبور نکرده است.", wave2),
            Hard("wave3-extreme", "موج ۳ از انتهای موج ۱ عبور کرده است.", wave3Extreme),
            Hard("wave3-shortest", "موج ۳ کوتاه‌ترین موج جنبشی نیست.", wave3),
            Hard("wave4-overlap", diagonal ? "هم‌پوشانی موج ۴ و ۱ با ساختار دیاگونال سازگار است." : "موج ۴ وارد محدوده موج ۱ نشده است.", !overlap || diagonal),
            Guide("alternation", "عمق موج‌های ۲ و ۴ اصل تناوب را تأیید می‌کند.", alternating),
            Guide("wave5-truncation", truncated ? "موج ۵ ناقص است و فقط پس از تکمیل قابل تأیید است." : "موج ۵ از انتهای موج ۳ عبور کرده است.", !truncated)
        };
        return Make(bull, diagonal ? "EndingDiagonal" : truncated ? "TruncatedImpulse" : "Impulse", "Complete",
            "اصلاح پس از موج ۵", score, p, ["0", "1", "2", "3", "4", "5"], rules, p[0].Price, p[4].Price,
            r2, e3, r4, truncated ? "موج پنجم ناقص؛ احتمال بازگشت تند بیشتر است." : "چرخه پنج‌موجی کامل شده و سناریوی اصلاح باید بررسی شود.");
    }

    private static ElliottScenario? ScoreDeveloping(IReadOnlyList<ElliottPivot> p)
    {
        if (p.Count is < 3 or > 5 || !Alternates(p)) return null;
        var bull = p[0].Kind == "Low";
        if (!Beyond(p[2].Price, p[0].Price, bull)) return null;
        var rules = new List<ElliottRule> { Hard("wave2-origin", "موج ۲ از مبدأ موج ۱ عبور نکرده است.", true) };
        decimal e3 = 0, r4 = 0; var diagonal = false;
        if (p.Count >= 4)
        {
            if (!Beyond(p[3].Price, p[1].Price, bull)) return null;
            e3 = Leg(p, 2) / Leg(p, 0); rules.Add(Hard("wave3-extreme", "موج ۳ از انتهای موج ۱ عبور کرده است.", true));
        }
        if (p.Count == 5)
        {
            var overlap = !Beyond(p[4].Price, p[1].Price, bull); diagonal = overlap && Leg(p, 2) < Leg(p, 0);
            if (overlap && !diagonal) return null;
            r4 = Leg(p, 3) / Leg(p, 2);
            rules.Add(Hard("wave4-overlap", diagonal ? "هم‌پوشانی موج ۴ با دیاگونال سازگار است." : "موج ۴ وارد محدوده موج ۱ نشده است.", true));
        }
        var r2 = Leg(p, 1) / Leg(p, 0); var current = p.Count switch { 3 => "موج ۳", 4 => "موج ۴", _ => "موج ۵" };
        var score = 48m + p.Count * 5 + Fib(r2, .5m, .618m, .786m) * 9
            + (e3 > 0 ? Fib(e3, 1m, 1.618m, 2.618m) * 8 : 0) + (r4 > 0 ? Fib(r4, .236m, .382m, .5m) * 6 : 0);
        return Make(bull, diagonal ? "DevelopingDiagonal" : "DevelopingImpulse", "Developing", current, score, p,
            Enumerable.Range(0, p.Count).Select(x => x.ToString()).ToArray(), rules, p[0].Price, p[^1].Price, r2, e3, r4,
            $"ساختار هنوز کامل نیست؛ محتمل‌ترین فاز فعلی {current} است و با پیوت بعدی تأیید یا باطل می‌شود.");
    }

    private static ElliottScenario? ScoreCorrection(IReadOnlyList<ElliottPivot> p)
    {
        if (p.Count != 4 || !Alternates(p)) return null;
        var a = Leg(p, 0); if (a == 0) return null;
        var down = p[0].Kind == "High"; var b = Leg(p, 1) / a; var c = Leg(p, 2) / a;
        var cBeyondA = down ? p[3].Price < p[1].Price : p[3].Price > p[1].Price;
        var bBeyondOrigin = down ? p[2].Price > p[0].Price : p[2].Price < p[0].Price;
        string pattern, note; decimal score;
        if (b <= .75m && cBeyondA) { pattern = "Zigzag"; score = 62 + Fib(c, .618m, 1m, 1.618m) * 18; note = "اصلاح زیگزاگ جهت‌دار و عمیق."; }
        else if (!bBeyondOrigin && b >= .8m) { pattern = "Flat"; score = 58 + Fib(b, .9m, 1m) * 12 + Fib(c, .618m, 1m) * 10; note = "فلت معمولی؛ موج B نزدیک مبدأ A برگشته است."; }
        else if (bBeyondOrigin && cBeyondA) { pattern = "ExpandedFlat"; score = 64 + Fib(b, 1.236m, 1.382m, 1.618m) * 12 + Fib(c, 1m, 1.618m) * 12; note = "فلت گسترش‌یافته؛ B از مبدأ A و C از انتهای A عبور کرده‌اند."; }
        else if (bBeyondOrigin) { pattern = "RunningFlat"; score = 52 + Fib(b, 1.236m, 1.382m) * 10; note = "فلت رانینگ؛ موج C از انتهای A عبور نکرده است."; }
        else return null;
        return Make(!down, pattern, "Complete", "پایان احتمالی موج C", score, p, ["0", "A", "B", "C"],
            [Hard("abc-alternation", "پیوت‌های A-B-C به‌صورت متناوب تشکیل شده‌اند.", true), Guide("correction-ratio", "نسبت‌های B و C با محدوده رایج الگو هم‌خوان‌اند.", score >= 65)],
            p[0].Price, p[2].Price, b, c, 0, note);
    }

    private static ElliottScenario? ScoreTriangle(IReadOnlyList<ElliottPivot> p)
    {
        if (p.Count != 6 || !Alternates(p)) return null;
        var legs = Enumerable.Range(0, 5).Select(i => Leg(p, i)).ToArray();
        var contracting = legs[2] < legs[0] && legs[3] < legs[1] && legs[4] < legs[2];
        var expanding = legs[2] > legs[0] && legs[3] > legs[1] && legs[4] > legs[2];
        if (!contracting && !expanding) return null;
        var ratio = (legs[2] / legs[0] + legs[3] / legs[1] + legs[4] / legs[2]) / 3;
        return Make(p[0].Kind == "Low", expanding ? "ExpandingTriangle" : "ContractingTriangle", "Complete",
            "حرکت نهایی پس از مثلث", 58 + Fib(ratio, contracting ? .618m : 1.618m) * 22, p,
            ["0", "A", "B", "C", "D", "E"],
            [Hard("triangle-five", "پنج شاخه A-B-C-D-E شناسایی شده است.", true), Guide("triangle-ratio", contracting ? "شاخه‌های متناوب در حال انقباض‌اند." : "شاخه‌های متناوب در حال گسترش‌اند.", true)],
            p[0].Price, p[4].Price, ratio, 0, 0, "مثلث پایان یافته؛ شکست E معمولاً آخرین حرکت هم‌جهت روند قبلی را آغاز می‌کند.");
    }

    private static ElliottScenario Make(bool bull, string pattern, string phase, string current, decimal score,
        IReadOnlyList<ElliottPivot> p, IReadOnlyList<string> labels, IReadOnlyList<ElliottRule> rules,
        decimal startInvalidation, decimal waveInvalidation, decimal r2, decimal e3, decimal r4, string summary) =>
        new(bull ? "Bullish" : "Bearish", Math.Round(Math.Clamp(score, 0, 100), 1),
            p.Select((x, i) => new ElliottWavePoint(labels[i], x.Time, x.Price)).ToArray(), rules,
            startInvalidation, waveInvalidation, new(Math.Round(r2, 3), Math.Round(e3, 3), Math.Round(r4, 3)),
            pattern, phase, current, summary);
    private static bool Alternates(IReadOnlyList<ElliottPivot> p) => p.Count > 1 && p.Zip(p.Skip(1)).All(x => x.First.Kind != x.Second.Kind);
    private static decimal Leg(IReadOnlyList<ElliottPivot> p, int i) => Math.Abs(p[i + 1].Price - p[i].Price);
    private static bool Beyond(decimal value, decimal boundary, bool bull) => bull ? value > boundary : value < boundary;
    private static ElliottRule Hard(string code, string text, bool pass) => new(code, text, pass, true);
    private static ElliottRule Guide(string code, string text, bool pass) => new(code, text, pass, false);
    private static decimal Fib(decimal value, params decimal[] targets) => Math.Max(0, 1 - targets.Min(x => Math.Abs(value - x) / Math.Max(x, .0001m)));
}

public sealed record ElliottAnalysis(IReadOnlyList<ElliottPivot> Pivots, IReadOnlyList<ElliottScenario> Scenarios, string Message, string RuleSetVersion);
public sealed record ElliottPivot(int Index, long Time, decimal Price, string Kind);
public sealed record ElliottScenario(string Direction, decimal Score, IReadOnlyList<ElliottWavePoint> Waves,
    IReadOnlyList<ElliottRule> Rules, decimal StartInvalidation, decimal Wave4Invalidation, ElliottRatios Ratios,
    string Pattern, string Phase, string CurrentWave, string Summary);
public sealed record ElliottWavePoint(string Label, long Time, decimal Price);
public sealed record ElliottRule(string Code, string Description, bool Passed, bool IsHard);
public sealed record ElliottRatios(decimal Wave2Retracement, decimal Wave3Extension, decimal Wave4Retracement);

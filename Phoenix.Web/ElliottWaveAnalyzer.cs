using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

/// <summary>Hard Elliott rules invalidate counts; ratios and alternation only rank them.</summary>
public sealed class ElliottWaveAnalyzer
{
    public const string RuleSetVersion = "4.3-verified-overlays";
    public ElliottProfile Profile { get; }
    public string CacheVersion => $"{RuleSetVersion}-{Profile}";
    public ElliottWaveAnalyzer() : this(Enum.TryParse<ElliottProfile>(
        Environment.GetEnvironmentVariable("PHOENIX_ELLIOTT_PROFILE"), true, out var value)
        && Enum.IsDefined(value) ? value : ElliottProfile.Classic) { }
    public ElliottWaveAnalyzer(ElliottProfile profile) { Profile = profile; }

    private readonly SemaphoreSlim _analysisGate = new(1, 1);

    // Keep CPU parsing off HTTP/Telegram pool threads and bound parallel CPU work.
    public async Task<ElliottAnalysis> AnalyzeAsync(IReadOnlyList<BybitKline> candles,
        CancellationToken token, int depth = 5, decimal deviationPercent = 0.6m)
    {
        await _analysisGate.WaitAsync(token);
        try
        {
            return await Task.Factory.StartNew(() => Analyze(candles, depth, deviationPercent, token),
                token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally { _analysisGate.Release(); }
    }

    public ElliottAnalysis Analyze(IReadOnlyList<BybitKline> candles, int depth = 5,
        decimal deviationPercent = 0.6m, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        candles = candles.GroupBy(c=>c.OpenTime).Select(g=>g.Last()).OrderBy(c=>c.OpenTime).ToArray();
        if (candles.Count < 30) return new([], [], "برای تحلیل حداقل ۳۰ کندل لازم است.", RuleSetVersion)
            { Coverage = new(0, 0, 0, 0, [], candles.Count < 2 ? [] : [new(candles[0].OpenTime, candles[^1].OpenTime, "InsufficientCandles")]) };
        // Elliott form is extracted from the line chart (Close), even when the
        // presentation requested by the user is candlesticks. Wicks must not
        // create a different count from the corresponding line chart.
        var pivots = FindPivots(candles, Math.Clamp(depth, 2, 20), Math.Clamp(deviationPercent, .05m, 20m));
        // Every change of direction between consecutive closes ends a raw leg.
        // The broader pivots above group these legs into Elliott degrees; raw
        // legs remain available to validate the subdivisions of each pattern.
        var detailPivots = CloseTurns(candles);
        var found = new List<ElliottScenario>();
        var validator = new ElliottStructureValidator(Profile, token);
        void Add(ElliottScenario? value) { if (value is not null) found.Add(value); }
        // Search structural degrees as well as the user's pivot view. A single
        // six-pivot sliding window cannot see a parent containing subwaves.
        var degrees = new[] { pivots.ToArray() }.Concat(ElliottPivotHierarchy.Degrees(detailPivots, token))
            .GroupBy(p => string.Join(',', p.Select(x => x.Time))).Select(g => g.First());
        void Search(IEnumerable<ElliottPivot[]> views)
        {
            foreach (var degree in views)
            {
                for (var start = 0; start < degree.Length; start++)
                {
                    token.ThrowIfCancellationRequested();
                    var remaining = degree.Length - start;
                    if (remaining >= 12) Add(ScoreTripleThree(degree.Skip(start).Take(12).ToArray()));
                    if (remaining >= 8) Add(ScoreDoubleThree(degree.Skip(start).Take(8).ToArray()));
                    if (remaining >= 6)
                    {
                        var six = degree.Skip(start).Take(6).ToArray();
                        Add(Profile == ElliottProfile.Copsey ? ScoreHarmonic(six) : ScoreImpulse(six));
                        if (Profile == ElliottProfile.Classic) { Add(ScoreDiagonal(six, false)); Add(ScoreDiagonal(six, true)); }
                        Add(ScoreTriangle(six)); Add(ScoreCombination(six));
                    }
                    if (remaining >= 4)
                    {
                        var four = degree.Skip(start).Take(4).ToArray();
                        Add(ScoreCorrection(four)); Add(ScoreCorrection(four, true)); Add(ScoreCombination(four));
                    }
                }
                foreach (var count in new[] { 5, 4, 3 })
                    if (Profile == ElliottProfile.Classic && degree.Length >= count) Add(ScoreDeveloping(degree.TakeLast(count).ToArray()));
            }
        }
        Search(degrees);
        // Inspect local endpoint-preserving degrees too. Reducing the whole
        // chart first can absorb E into the following rally before the five
        // corrective legs have been recognized as a triangle.
        foreach (var count in new[] { 16, 18, 20, 22, 24, 32, 48, 64 })
            for (var start = 0; start + count <= detailPivots.Count; start++)
                foreach (var view in ElliottPivotHierarchy.Degrees(detailPivots.Skip(start).Take(count).ToArray(), token))
                    { token.ThrowIfCancellationRequested(); if (view.Length == 6) Add(ScoreTriangle(view)); }
        // E of a triangle need not be its deepest/highest point. Ordinary
        // extreme reduction cannot recover that parent boundary. Promote a
        // fully parsed triangle as a single candidate leg, then validate its
        // actual position inside the parent (a triangle in wave 2 still fails).
        var triangleLegs = found.Where(s => s.Pattern.EndsWith("Triangle"))
            .DistinctBy(s => (s.Waves[0].Time, s.Waves[^1].Time))
            .OrderByDescending(s => s.Waves[^1].Time - s.Waves[0].Time).Take(96)
            .Where(s => validator.Validate(s, detailPivots, ElliottPosition.Wave4).ValidationStatus == "Verified")
            .Take(16).ToArray();
        foreach (var triangle in triangleLegs)
        {
            var grouped = detailPivots.Where(p => p.Time <= triangle.Waves[0].Time || p.Time >= triangle.Waves[^1].Time).ToArray();
            Search(ElliottPivotHierarchy.Degrees(grouped, token));
        }
        var decorated = found.GroupBy(x => $"{x.Pattern}|{string.Join(',', x.Waves.Select(w => w.Time))}")
            .Select(x => x.MaxBy(y => y.Score)!)
            // Spend the bounded validation work on broad candidates first,
            // then reserve space for recent local continuations. Cheap geometry
            // candidates must not starve the live structures of a shared budget.
            .OrderByDescending(x => x.Waves[^1].Time - x.Waves[0].Time)
            .Take(192).Concat(found.OrderByDescending(x => x.Waves[^1].Time)
                .ThenByDescending(x => x.Score).Take(64))
            // Reserve validation capacity across the full history, including
            // early patterns separated from the live count by an unknown span.
            .Concat(found.GroupBy(x => Math.Min(7, (int)(8d * (x.Waves[0].Time - candles[0].OpenTime) /
                    Math.Max(1d, candles[^1].OpenTime - candles[0].OpenTime))))
                .SelectMany(g => g.OrderByDescending(x => x.Waves[^1].Time - x.Waves[0].Time)
                    .ThenByDescending(x => x.Score).Take(8)))
            .DistinctBy(x => $"{x.Pattern}|{string.Join(',', x.Waves.Select(w => w.Time))}")
            .Select(x => ElliottGuidelines.Apply(validator.Validate(x, detailPivots)))
            .Where(x => x.ValidationStatus != "Invalid")
            .ToArray();
        var ranked = decorated
            .OrderByDescending(x => x.ValidationStatus == "Verified")
            .ThenByDescending(x => x.CoveragePercent)
            .ThenByDescending(x => x.SubdivisionCoveragePercent)
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.Waves[^1].Time).Take(5).ToArray();
        // The chart overlay must describe the live/right-hand side of the chart.
        // A high-scoring completed structure in the past is useful context, but
        // must never hide a valid developing count that reaches the newest pivot.
        // Developing counts still pass all hard rules in ScoreDeveloping.
        if (decorated.Length > 0)
        {
            var newestEnd = decorated.Max(x => x.Waves[^1].Time);
            var live = decorated.Where(x => x.Waves[^1].Time == newestEnd)
                .OrderByDescending(x => x.ValidationStatus == "Verified")
                .ThenByDescending(x => x.SubdivisionCoveragePercent)
                .ThenByDescending(x => x.CoveragePercent).ThenByDescending(x => x.Score).First();
            // Preserve a verified broad parent even when a small correction is
            // newest. Continuations join at the exact boundary, never by merely
            // stacking unrelated overlapping ABC/WXY candidates.
            var broad = ranked[0];
            var active = broad.ValidationStatus == "Verified" && broad.CoveragePercent > live.CoveragePercent
                ? broad : live;
            var context = new List<ElliottWavePoint>();
            var subwaves = active.Subwaves.ToList();
            void Include(ElliottScenario value)
            {
                // Verified roots are selected globally by ElliottCoverage. Keep
                // only a developing continuation here, with its own identity.
                if (value.ValidationStatus == "Verified") return;
                var id = ElliottCoverage.CountId(value);
                context.AddRange(value.Waves.Select(w => w with { Parent = id, Origin = "Continuation" }));
                subwaves.AddRange(value.Subwaves.Select(w => w with { Parent = $"{id}/{w.Parent}", Origin = "Continuation" }));
            }
            var cursor = active.Waves[0].Time;
            while (decorated.Where(x => x.ValidationStatus == "Verified" && x.Phase == "Complete" &&
                    x.Waves[^1].Time == cursor && x.Waves[0].Time < cursor)
                .OrderByDescending(x => x.CoveragePercent).ThenByDescending(x => x.Score).FirstOrDefault() is { } preceding)
            {
                Include(preceding); cursor = preceding.Waves[0].Time;
            }
            cursor = active.Waves[^1].Time;
            while (decorated.Where(x => x.Waves[0].Time == cursor && x.Waves[^1].Time > cursor &&
                    (x.ValidationStatus == "Verified" || x.Waves[^1].Time == newestEnd))
                .OrderByDescending(x => x.ValidationStatus == "Verified")
                .ThenByDescending(x => x.CoveragePercent).ThenByDescending(x => x.Score).FirstOrDefault() is { } following)
            {
                Include(following); cursor = following.Waves[^1].Time;
            }
            active = active with { ContextWaves = context.OrderBy(w => w.Time).ToArray(), Subwaves = subwaves };
            ranked = new[] { active }.Concat(ranked.Where(x =>
                    x.Pattern != active.Pattern || !x.Waves.Select(w => w.Time).SequenceEqual(active.Waves.Select(w => w.Time))))
                .Take(5).ToArray();
        }
        var message = ranked.Length == 0
            ? "ساختار معتبر پیدا نشد؛ پیوت‌های مهم برای بررسی دستی نمایش داده شده‌اند."
            : "قواعد ساختاری و ریزموج‌ها جدا از راهنماها بررسی شدند؛ سناریوی تأییدنشده قطعی نیست و امتیاز الگو احتمال تارگت نیست.";
        token.ThrowIfCancellationRequested();
        var coverage = ElliottCoverage.Build(decorated, candles.Select(c => c.OpenTime).ToArray(), ranked.FirstOrDefault());
        ranked = ranked.Select((scenario, index) => ElliottCoverage.Attach(scenario,
            index == 0 ? coverage : ElliottCoverage.Build(decorated, candles.Select(c => c.OpenTime).ToArray(), scenario))).ToArray();
        return new(pivots, ranked, message, RuleSetVersion) { Coverage = coverage };
    }

    /// <summary>Only closed bars may confirm a turn. Monthly bars use calendar months.</summary>
    public static IReadOnlyList<BybitKline> ClosedCandles(IReadOnlyList<BybitKline> candles,
        string interval, DateTimeOffset asOf)
    {
        var tier=interval.ToUpperInvariant();
        return candles.Where(c=>
        {
            var start=DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTime);
            var end=tier switch
            {
                "M" => start.AddMonths(1), "W" => start.AddDays(7), "D" => start.AddDays(1),
                _ => int.TryParse(tier,out var minutes) && minutes>0 ? start.AddMinutes(minutes) : DateTimeOffset.MaxValue
            };
            return end<=asOf;
        }).ToArray();
    }

    private static List<ElliottPivot> FindPivots(IReadOnlyList<BybitKline> candles, int depth, decimal deviation)
    {
        var result = new List<ElliottPivot>();
        AddPivot(result, new(0, candles[0].OpenTime, candles[0].Close, "Boundary"), 0m);
        for (var i = depth; i < candles.Count - depth; i++)
        {
            var close = candles[i].Close;
            var high = Enumerable.Range(i - depth, depth * 2 + 1).Where(j => j != i).All(j => close > candles[j].Close);
            var low = Enumerable.Range(i - depth, depth * 2 + 1).Where(j => j != i).All(j => close < candles[j].Close);
            if (high) AddPivot(result, new(i, candles[i].OpenTime, close, "High"), deviation);
            if (low) AddPivot(result, new(i, candles[i].OpenTime, close, "Low"), deviation);
        }
        AddPivot(result, new(candles.Count - 1, candles[^1].OpenTime, candles[^1].Close, "Boundary"), deviation);
        NormalizeBoundaryKinds(result);
        return result;
    }

    public static List<ElliottPivot> CloseTurns(IReadOnlyList<BybitKline> candles)
    {
        var turns = new List<ElliottPivot>();
        if (candles.Count == 0) return turns;
        turns.Add(new ElliottPivot(0, candles[0].OpenTime, candles[0].Close, "Boundary"));
        var direction = 0;
        for (var i = 1; i < candles.Count; i++)
        {
            var next = Math.Sign(candles[i].Close - candles[i - 1].Close);
            if (next == 0) continue;
            if (direction != 0 && next != direction)
                turns.Add(new ElliottPivot(i - 1, candles[i - 1].OpenTime,
                    candles[i - 1].Close, direction > 0 ? "High" : "Low"));
            direction = next;
        }
        if (turns[^1].Index != candles.Count - 1)
            turns.Add(new ElliottPivot(candles.Count - 1, candles[^1].OpenTime,
                candles[^1].Close, "Boundary"));
        NormalizeBoundaryKinds(turns);
        return turns;
    }

    private static void NormalizeBoundaryKinds(List<ElliottPivot> pivots)
    {
        if (pivots.Count < 2) return;
        if (pivots[0].Kind == "Boundary")
            pivots[0] = pivots[0] with { Kind = pivots[0].Price <= pivots[1].Price ? "Low" : "High" };
        if (pivots[^1].Kind == "Boundary")
            pivots[^1] = pivots[^1] with { Kind = pivots[^1].Price >= pivots[^2].Price ? "High" : "Low" };
        for (var i = pivots.Count - 2; i >= 0; i--)
            if (pivots[i].Kind == pivots[i + 1].Kind)
            {
                var keepRight = pivots[i].Kind == "High"
                    ? pivots[i + 1].Price >= pivots[i].Price
                    : pivots[i + 1].Price <= pivots[i].Price;
                pivots.RemoveAt(keepRight ? i : i + 1);
            }
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
        if (overlap) return null;
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
            Hard("wave4-overlap", "موج ۴ وارد محدوده موج ۱ نشده است.", !overlap),
            Guide("alternation", "عمق موج‌های ۲ و ۴ اصل تناوب را تأیید می‌کند.", alternating),
            Guide("wave5-truncation", truncated ? "موج ۵ ناقص است و فقط پس از تکمیل قابل تأیید است." : "موج ۵ از انتهای موج ۳ عبور کرده است.", !truncated)
        };
        return Make(bull, truncated ? "TruncatedImpulse" : "Impulse", "Complete",
            "اصلاح پس از موج ۵", score, p, ["0", "1", "2", "3", "4", "5"], rules, p[0].Price, p[4].Price,
            r2, e3, r4, truncated ? "موج پنجم ناقص؛ احتمال بازگشت تند بیشتر است." : "چرخه پنج‌موجی کامل شده و سناریوی اصلاح باید بررسی شود.");
    }

    private static ElliottScenario? ScoreDiagonal(IReadOnlyList<ElliottPivot> p, bool leading)
    {
        var pattern = leading ? "LeadingDiagonal" : "EndingDiagonal";
        if (!ElliottStructureValidator.Geometry(pattern, p)) return null;
        return Make(p[0].Kind == "Low", pattern, "Unverified", "بررسی جایگاه دیاگونال", 55, p,
            ["0","1","2","3","4","5"],
            [Hard("diagonal-geometry", "مرزهای گوه و نسبت طول شاخه‌ها با نوع همگرا/واگرا سازگارند.", true)],
            p[0].Price,p[2].Price,Leg(p,1)/Leg(p,0),Leg(p,2)/Leg(p,0),Leg(p,3)/Leg(p,2),
            "دیاگونال فقط پس از تأیید ریزساختار و جایگاه در والد معتبر است.");
    }

    private static ElliottScenario? ScoreHarmonic(IReadOnlyList<ElliottPivot> p)
    {
        if (!ElliottStructureValidator.Geometry("HarmonicImpulse",p)) return null;
        var r2=Leg(p,1)/Leg(p,0); var e3=Leg(p,2)/Leg(p,0); var r4=Leg(p,3)/Leg(p,2);
        var v=Leg(p,4)/Math.Abs(p[3].Price-p[0].Price);
        var score=50+10*Fib(r2,.146m,.382m,.5m,.618m,.764m)
            +15*Fib(e3,1.764m,1.854m,1.9002m,2.236m,2.764m,2.854m)
            +10*Fib(r4,.146m,.236m,.333m,.382m,.414m,.5m,.586m)
            +10*Fib(v,.618m,.667m,.764m,1m,1.144m,1.236m,1.382m);
        return Make(p[0].Kind=="Low","HarmonicImpulse","Unverified","ساختار تعدیل‌شده کوپسی",score,p,
            ["0","1","2","3","4","5"],
            [Hard("copsey-origin", "موج ii از مبدأ i عبور نکرده و v از iii گذشته است.",true),
             Guide("copsey-ratios", "نسبت‌های روش کوپسی فقط برای رتبه‌بندی به کار رفته‌اند.",true)],
            p[0].Price,p[2].Price,r2,e3,r4,"ساختار کوپسی؛ قابل ادغام با ایمپالس کلاسیک نیست.");
    }

    private static ElliottScenario? ScoreDeveloping(IReadOnlyList<ElliottPivot> p)
    {
        if (p.Count is < 3 or > 5 || !Alternates(p)) return null;
        var bull = p[0].Kind == "Low";
        if (!Beyond(p[2].Price, p[0].Price, bull)) return null;
        var rules = new List<ElliottRule> { Hard("wave2-origin", "موج ۲ از مبدأ موج ۱ عبور نکرده است.", true) };
        decimal e3 = 0, r4 = 0;
        if (p.Count >= 4)
        {
            if (!Beyond(p[3].Price, p[1].Price, bull)) return null;
            e3 = Leg(p, 2) / Leg(p, 0); rules.Add(Hard("wave3-extreme", "موج ۳ از انتهای موج ۱ عبور کرده است.", true));
        }
        if (p.Count == 5)
        {
            var overlap = !Beyond(p[4].Price, p[1].Price, bull);
            if (overlap) return null;
            r4 = Leg(p, 3) / Leg(p, 2);
            rules.Add(Hard("wave4-overlap", "موج ۴ وارد محدوده موج ۱ نشده است.", true));
        }
        var r2 = Leg(p, 1) / Leg(p, 0); var current = p.Count switch { 3 => "موج ۳", 4 => "موج ۴", _ => "موج ۵" };
        var score = 48m + p.Count * 5 + Fib(r2, .5m, .618m, .786m) * 9
            + (e3 > 0 ? Fib(e3, 1m, 1.618m, 2.618m) * 8 : 0) + (r4 > 0 ? Fib(r4, .236m, .382m, .5m) * 6 : 0);
        return Make(bull, "DevelopingImpulse", "Developing", current, score, p,
            Enumerable.Range(0, p.Count).Select(x => x.ToString()).ToArray(), rules, p[0].Price, p[^1].Price, r2, e3, r4,
            $"ساختار هنوز کامل نیست؛ محتمل‌ترین فاز فعلی {current} است و با پیوت بعدی تأیید یا باطل می‌شود.");
    }

    private static ElliottScenario? ScoreCorrection(IReadOnlyList<ElliottPivot> p, bool preferFlat = false)
    {
        if (p.Count != 4 || !Alternates(p)) return null;
        var a = Leg(p, 0); if (a == 0) return null;
        var down = p[0].Kind == "High"; var b = Leg(p, 1) / a; var c = Leg(p, 2) / a;
        var cBeyondA = down ? p[3].Price < p[1].Price : p[3].Price > p[1].Price;
        var bBeyondOrigin = down ? p[2].Price > p[0].Price : p[2].Price < p[0].Price;
        string pattern, note; decimal score;
        if (!preferFlat && !bBeyondOrigin && b < 1m && cBeyondA) { pattern = "Zigzag"; score = 62 + Fib(c, .618m, 1m, 1.618m) * 18; note = "اصلاح زیگزاگ جهت‌دار و عمیق."; }
        else if (!bBeyondOrigin) { pattern = "Flat"; score = 58 + Fib(b, .9m, 1m) * 12 + Fib(c, .618m, 1m) * 10; note = "فلت معمولی؛ موج B نزدیک مبدأ A برگشته است."; }
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

    private static ElliottScenario? ScoreCombination(IReadOnlyList<ElliottPivot> p)
    {
        if (p.Count is not (4 or 6) || !Alternates(p)) return null;
        return Make(p[0].Kind == "Low", p.Count == 4 ? "DoubleThree" : "TripleThree", "Unverified",
            "بررسی اصلاح مرکب", 50, p, p.Count == 4 ? ["0","W","X","Y"] : ["0","W","X","Y","X","Z"],
            [], p[0].Price,p[^2].Price,0,0,0,"هر جزء اصلاح و رابط X باید از ابتدا تا انتها اعتبارسنجی شود.");
    }

    private static ElliottScenario? ScoreDoubleThree(IReadOnlyList<ElliottPivot> p)
    {
        if (p.Count != 8 || !Alternates(p)) return null;
        var w = ScoreCorrection(p.Take(4).ToArray());
        var y = ScoreCorrection(p.Skip(4).Take(4).ToArray());
        if (w is null || y is null) return null;
        var down = p[0].Kind == "High";
        var progresses = down ? p[7].Price < p[3].Price : p[7].Price > p[3].Price;
        // Sideways combinations need not deepen like a double zigzag.

        var score = 60m + (w.Score + y.Score) / 10m;
        return Make(!down, "DoubleThree", "Complete", "پایان احتمالی Y", score, p,
            ["0", "A", "B", "W", "X", "A", "B", "Y"],
            [Hard("double-three-components", "دو ساختار اصلاحی معتبر با موج رابط X تشکیل شده‌اند.", true),
             Guide("double-three-progress", "موج Y اصلاح را در جهت W ادامه داده است.", progresses)],
            p[0].Price, p[4].Price, 0, 0, 0,
            "اصلاح مرکب W-X-Y؛ اجزای داخلی W و Y نیز جداگانه اعتبارسنجی شده‌اند.");
    }

    private static ElliottScenario? ScoreTripleThree(IReadOnlyList<ElliottPivot> p)
    {
        if (p.Count != 12 || !Alternates(p)) return null;
        var w = ScoreCorrection(p.Take(4).ToArray());
        var y = ScoreCorrection(p.Skip(4).Take(4).ToArray());
        var z = ScoreCorrection(p.Skip(8).Take(4).ToArray());
        if (w is null || y is null || z is null) return null;
        var down = p[0].Kind == "High";
        var progresses = down ? p[11].Price < p[7].Price : p[11].Price > p[7].Price;
        // Sideways combinations need not deepen like a double zigzag.

        var score = 58m + (w.Score + y.Score + z.Score) / 15m;
        return Make(!down, "TripleThree", "Complete", "پایان احتمالی Z", score, p,
            ["0", "A", "B", "W", "X", "A", "B", "Y", "X", "A", "B", "Z"],
            [Hard("triple-three-components", "سه ساختار اصلاحی معتبر با دو موج رابط X تشکیل شده‌اند.", true),
             Guide("triple-three-progress", "موج Z اصلاح را در جهت ساختار قبلی ادامه داده است.", progresses)],
            p[0].Price, p[8].Price, 0, 0, 0,
            "اصلاح مرکب W-X-Y-X-Z؛ سه جزء اصلاحی داخلی جداگانه اعتبارسنجی شده‌اند.");
    }

    private static ElliottScenario Make(bool bull, string pattern, string phase, string current, decimal score,
        IReadOnlyList<ElliottPivot> p, IReadOnlyList<string> labels, IReadOnlyList<ElliottRule> rules,
        decimal startInvalidation, decimal waveInvalidation, decimal r2, decimal e3, decimal r4, string summary) =>
        new(bull ? "Bullish" : "Bearish", Math.Round(Math.Clamp(score, 0, 100), 1),
            p.Select((x, i) => new ElliottWavePoint(labels[i], x.Time, x.Price)).ToArray(), rules,
            startInvalidation, waveInvalidation, new(Math.Round(r2, 3), Math.Round(e3, 3), Math.Round(r4, 3)),
            pattern, phase, current, summary);

    private static bool Alternates(IReadOnlyList<ElliottPivot> p) => p.Count > 1 && p.Zip(p.Skip(1)).All(x => x.First.Kind != x.Second.Kind && x.First.Price != x.Second.Price && x.First.Time < x.Second.Time);
    private static decimal Leg(IReadOnlyList<ElliottPivot> p, int i) => Math.Abs(p[i + 1].Price - p[i].Price);
    private static bool Beyond(decimal value, decimal boundary, bool bull) => bull ? value > boundary : value < boundary;
    private static ElliottRule Hard(string code, string text, bool pass) => new(code, text, pass, true) { Source = code.StartsWith("copsey") ? "ه:۳–۶" : "ف:۶–۷؛ ب:۱۴–۱۸" };
    private static ElliottRule Guide(string code, string text, bool pass) => new(code, text, pass, false);
    private static decimal Fib(decimal value, params decimal[] targets) => Math.Max(0, 1 - targets.Min(x => Math.Abs(value - x) / Math.Max(x, .0001m)));
}

public sealed record ElliottAnalysis(IReadOnlyList<ElliottPivot> Pivots, IReadOnlyList<ElliottScenario> Scenarios, string Message, string RuleSetVersion)
{
    public ElliottCoverageReport? Coverage { get; init; }
}
public sealed record ElliottPivot(int Index, long Time, decimal Price, string Kind);
public sealed record ElliottScenario(string Direction, decimal Score, IReadOnlyList<ElliottWavePoint> Waves,
    IReadOnlyList<ElliottRule> Rules, decimal StartInvalidation, decimal Wave4Invalidation, ElliottRatios Ratios,
    string Pattern, string Phase, string CurrentWave, string Summary)
{
    public IReadOnlyList<ElliottWavePoint> Subwaves { get; init; } = [];
    public IReadOnlyList<ElliottWavePoint> ContextWaves { get; init; } = [];
    /// <summary>Range coverage only; not proof of valid subdivisions.</summary>
    public decimal CoveragePercent { get; init; }
    public decimal SubdivisionCoveragePercent { get; init; }
    public string ValidationStatus { get; init; } = "Unverified";
    public string Profile { get; init; } = "Classic";
    public bool SearchLimitReached { get; init; }
    public ElliottStructureNode? Structure { get; init; }
    public ElliottCoverageReport? Coverage { get; init; }
}
public sealed record ElliottWavePoint(string Label, long Time, decimal Price, int Degree = 0, string? Parent = null)
{
    public string? Timeframe { get; init; }
    public string Origin { get; init; } = "Active";
    public bool IsTentative { get; init; }
    public string ValidationStatus { get; init; } = "Unverified";
}
public sealed record ElliottRule(string Code, string Description, bool Passed, bool IsHard)
{
    public string Status { get; init; } = Passed ? "Passed" : "Failed";
    public string? Source { get; init; }
}
public sealed record ElliottRatios(decimal Wave2Retracement, decimal Wave3Extension, decimal Wave4Retracement);

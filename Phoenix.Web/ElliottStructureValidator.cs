namespace Phoenix.Web;

public enum ElliottProfile { Classic, Copsey }
public enum ElliottPosition { Unknown, Wave1, Wave2, Wave3, Wave4, Wave5, A, B, C, X, CombinationEnd }

/// <summary>A full-span parse. Atomic leaves mean the smallest observed close leg, not an invented subdivision.</summary>
public sealed record ElliottStructureNode(string Pattern, long Start, long End,
    IReadOnlyList<ElliottStructureNode> Children);

/// <summary>
/// Source rules: Persian pp.5-22,48-51; Balan PDF pp.14-18,38-45;
/// Copsey handout pp.5-16. Ratios never rescue an invalid structural parse.
/// Parsing is bounded, endpoint-preserving and local to a single analysis.
/// </summary>
public sealed class ElliottStructureValidator(ElliottProfile profile = ElliottProfile.Classic)
{
    private readonly Dictionary<string, ElliottStructureNode?> _memo = new();
    private int _remaining = 12000;

    public ElliottScenario Validate(ElliottScenario scenario, IReadOnlyList<ElliottPivot> raw,
        ElliottPosition position = ElliottPosition.Unknown)
    {
        var components = Components(scenario);
        var boundaryView = scenario.Waves.Select((w,i)=>new ElliottPivot(i,w.Time,w.Price,
            i%2==0 ? (scenario.Waves[1].Price>scenario.Waves[0].Price ? "Low" : "High") : (scenario.Waves[1].Price>scenario.Waves[0].Price ? "High" : "Low"))).ToArray();
        var pathValid = scenario.Waves.Count != 6 || PathRules(scenario.Pattern,boundaryView,raw);
        var shapeValid = scenario.Pattern.StartsWith("Developing") || scenario.Pattern is "DoubleThree" or "TripleThree"
            || Geometry(scenario.Pattern,boundaryView);
        var children = new List<ElliottStructureNode>();
        var details = new List<ElliottWavePoint>();
        var rules = scenario.Rules.ToList();
        var checkedLegs = 0;
        foreach (var (start, end, role, where) in components)
        {
            var a = scenario.Waves[start]; var b = scenario.Waves[end];
            var inside = raw.Where(p => p.Time >= a.Time && p.Time <= b.Time).ToArray();
            // Never select only the best-looking window inside a parent.
            var exact = inside.Length >= 2 && inside[0].Time == a.Time && inside[^1].Time == b.Time
                && inside[0].Price == a.Price && inside[^1].Price == b.Price;
            var child = exact ? Match(inside, role, where, 0, allowAtomic: false) : null;
            rules.Add(new ElliottRule($"subdivision-{start}-{end}", child is null
                ? $"ریزساختار کاملِ {b.Label} هنوز تأیید نشده است."
                : $"ریزساختار {b.Label} از ابتدا تا انتهای همان موج تأیید شد ({child.Pattern}).", child is not null, true)
                { Status = child is null ? "Unknown" : "Passed", Source = "ف:۵،۱۹–۲۲؛ ب:۱۴–۱۸،۴۳" });
            if (child is null) continue;
            checkedLegs++;
            children.Add(child);
            AddLabels(child, $"{start}:{b.Label}", 1, details, raw);
        }
        var placement = PlacementAllowed(scenario.Pattern, position);
        var placementKnown = !NeedsPosition(scenario.Pattern) || position != ElliottPosition.Unknown;
        if (!placementKnown || !placement)
            rules.Add(new ElliottRule("parent-position", placementKnown
                ? "این الگو در جایگاه موج والد مجاز نیست." : "جایگاه الگو در موج والد هنوز تأیید نشده است.", false, true)
                { Status = placementKnown ? "Failed" : "Unknown", Source = "ب:۱۵–۱۶،۴۳؛ ف:۱۳،۴۹" });
        var copseyBoundary = true;
        if (scenario.Pattern == "HarmonicImpulse" && children.Count == components.Count && children.Count >= 4)
        {
            var third = children[2];
            // iv may not pass the endpoint of b within iii. No ambiguous ratio is used.
            var fifth=children[4];
            var a5End=fifth.Children.Count>=1 ? raw.FirstOrDefault(p=>p.Time==fifth.Children[0].End) : null;
            var bEnd = third.Children.Count >= 2 ? third.Children[1].End : (long?)null;
            var pivot = raw.FirstOrDefault(p => p.Time == bEnd);
            copseyBoundary = pivot is not null && a5End is not null
                && Directed(scenario.Waves[3].Price-a5End.Price,scenario.Waves[1].Price>scenario.Waves[0].Price)>0 && Directed(scenario.Waves[4].Price - pivot.Price,
                scenario.Waves[1].Price > scenario.Waves[0].Price) > 0;
            rules.Add(new ElliottRule("copsey-iv-biii", "مرز iv نسبت به b داخلی iii و پایان a از v نسبت به iii رعایت شده است.", copseyBoundary, true)
                { Source = "ه:۶،۱۳" });
        }
        var full = components.Count > 0 && checkedLegs == components.Count;
        var latestIsOpen = raw.Count > 0 && scenario.Waves[^1].Time == raw[^1].Time;
        var profileValid = scenario.Pattern != "HarmonicImpulse" || profile == ElliottProfile.Copsey;
        if (!pathValid || !shapeValid || !profileValid)
            rules.Add(new ElliottRule("structural-validity", "ساختار، مرزهای داخلی یا پروفایل با قواعد انتخاب‌شده سازگار نیست.", false, true)
                { Source="ف:۶–۷؛ ب:۱۴؛ ه:۳–۶" });
        var valid = placement && copseyBoundary && pathValid && shapeValid && profileValid;
        var status = !valid ? "Invalid" : full && placementKnown ? "Verified" : "Unverified";
        var phase = scenario.Phase == "Developing" || latestIsOpen ? "Developing"
            : status == "Verified" ? "Complete" : "Unverified";
        var span = raw.Count(p => p.Time >= scenario.Waves[0].Time && p.Time <= scenario.Waves[^1].Time);
        return scenario with
        {
            Profile = profile.ToString(), ValidationStatus = status, Phase = phase,
            Rules = rules,
            SearchLimitReached = _remaining <= 0,
            Waves = scenario.Waves.Select(w=>w with { IsTentative = phase != "Complete" }).ToArray(),
            Structure = new(scenario.Pattern, scenario.Waves[0].Time, scenario.Waves[^1].Time, children),
            Subwaves = details, SubdivisionCoveragePercent = components.Count == 0 ? 0 : Math.Round(100m * checkedLegs / components.Count, 1),
            CoveragePercent = raw.Count < 2 ? 0 : Math.Round(100m * span / raw.Count, 1),
            Summary = phase == "Complete" ? scenario.Summary
                : phase == "Developing" ? "سناریوی در حال تشکیل؛ ساق جاری و پایان الگو قطعی نیست."
                : "سناریوی اولیه؛ ریزساختار یا جایگاه والد هنوز تأیید نشده است."
        };
    }

    private List<(int Start, int End, string Role, ElliottPosition Position)> Components(ElliottScenario s)
    {
        if (s.Pattern is "DoubleThree" or "TripleThree" && s.Waves.Count is 4 or 6)
            return Enumerable.Range(0,s.Waves.Count-1).Select(i => (i,i+1,"Correction",
                i%2==1 ? ElliottPosition.X : i==s.Waves.Count-2 ? ElliottPosition.CombinationEnd : ElliottPosition.Wave2)).ToList();
        if (s.Pattern == "DoubleThree") return [(0,3,"Correction",ElliottPosition.Wave2),(3,4,"Correction",ElliottPosition.X),(4,7,"Correction",ElliottPosition.CombinationEnd)];
        if (s.Pattern == "TripleThree") return [(0,3,"Correction",ElliottPosition.Wave2),(3,4,"Correction",ElliottPosition.X),(4,7,"Correction",ElliottPosition.Wave2),(7,8,"Correction",ElliottPosition.X),(8,11,"Correction",ElliottPosition.CombinationEnd)];
        var result = new List<(int,int,string,ElliottPosition)>();
        for (var i = 0; i < s.Waves.Count - 1; i++)
        {
            var label = s.Waves[i + 1].Label;
            var pos = label switch { "1" => ElliottPosition.Wave1, "2" => ElliottPosition.Wave2,
                "3" => ElliottPosition.Wave3, "4" => ElliottPosition.Wave4, "5" => ElliottPosition.Wave5,
                "A" => ElliottPosition.A, "B" => ElliottPosition.B, "C" => ElliottPosition.C, _ => ElliottPosition.Unknown };
            var role = s.Pattern switch
            {
                "HarmonicImpulse" => i % 2 == 0 ? "Zigzag" : "Correction",
                "EndingDiagonal" => "Zigzag",
                "LeadingDiagonal" => i % 2 == 0 ? "Impulse" : "Correction",
                "Zigzag" => i == 1 ? "Correction" : "Motive",
                "Flat" or "ExpandedFlat" or "RunningFlat" => i == 2 ? "Motive" : "Correction",
                "ContractingTriangle" or "ExpandingTriangle" => "Correction",
                _ => i % 2 == 0 ? "Motive" : "Correction"
            };
            result.Add((i,i+1,role,pos));
        }
        return result;
    }

    private ElliottStructureNode? Match(ElliottPivot[] p, string role, ElliottPosition position, int depth, bool allowAtomic)
    {
        if (p.Length < 2 || p[0].Price == p[^1].Price) return null;
        if (p.Length == 2) return allowAtomic ? new("ObservedLeg",p[0].Time,p[1].Time,[]) : null;
        if (depth > 5 || --_remaining < 0) return null;
        // Prefer the fully observed five over a coarsened three. Otherwise a
        // clear impulse can be relabelled as a flat merely by dropping two turns.
        if (role == "Correction" && p.Length == 6 && Geometry("Impulse",p)) return null;
        var key = $"{p[0].Time}:{p[^1].Time}:{role}:{position}:{depth}";
        if (_memo.TryGetValue(key, out var cached)) return cached;
        foreach (var view in Views(p))
        {
            foreach (var pattern in Patterns(role, position))
            {
                if (!Geometry(pattern, view) || !PlacementAllowed(pattern, position) || !PathRules(pattern,view,p)) continue;
                var roles = ChildRoles(pattern, view.Length - 1);
                var nodes = new List<ElliottStructureNode>();
                var ok = true;
                for (var i = 0; i < view.Length - 1; i++)
                {
                    var sub = p.Where(x => x.Time >= view[i].Time && x.Time <= view[i+1].Time).ToArray();
                    // A grouped impulse cannot hide an internal origin/overlap violation.

                    var (childRole, childPosition) = roles[i];
                    var child = Match(sub, childRole, childPosition, depth+1, pattern is not ("DoubleThree" or "TripleThree"));
                    if (child is null) { ok = false; break; }
                    nodes.Add(child);
                }
                if (ok) return _memo[key] = new(pattern,p[0].Time,p[^1].Time,nodes);
            }
        }
        return _memo[key] = null;
    }

    private static bool PathRules(string pattern, IReadOnlyList<ElliottPivot> view, IReadOnlyList<ElliottPivot> raw)
    {
        if (pattern is not ("Impulse" or "TruncatedImpulse" or "LeadingDiagonal" or "EndingDiagonal" or "HarmonicImpulse")) return true;
        var bull=view[1].Price>view[0].Price;
        bool Above(decimal value,decimal boundary)=>Directed(value-boundary,bull)>0;
        return raw.Where(p=>p.Time>=view[1].Time && p.Time<=view[2].Time).All(p=>Above(p.Price,view[0].Price))
            && raw.Where(p=>p.Time>=view[3].Time && p.Time<=view[4].Time)
                .All(p=>Above(p.Price,pattern is "Impulse" or "TruncatedImpulse" ? view[1].Price : view[2].Price));
    }

    // Remove the least significant PAIR of turns, retaining both endpoints and
    // every raw leg in the child spans. No arbitrary window or stride sampling.
    private static IEnumerable<ElliottPivot[]> Views(ElliottPivot[] raw)
    {
        var p = raw.ToList();
        while (p.Count >= 4)
        {
            if (p.Count is 4 or 6) yield return p.ToArray();
            if (p.Count <= 4) yield break;
            var index = -1; var size = decimal.MaxValue;
            for (var i = 1; i < p.Count - 2; i++)
            {
                var amplitude = Math.Abs(p[i+1].Price-p[i].Price);
                if (amplitude < size) { index=i; size=amplitude; }
            }
            if (index < 0) yield break;
            p.RemoveRange(index,2);
        }
    }

    private static IEnumerable<string> Patterns(string role, ElliottPosition position) => role switch
    {
        "Motive" => position is ElliottPosition.Wave1 or ElliottPosition.A ? ["Impulse","LeadingDiagonal"]
            : position is ElliottPosition.Wave5 or ElliottPosition.C ? ["Impulse","EndingDiagonal"] : ["Impulse"],
        "Correction" => position is ElliottPosition.Wave4 or ElliottPosition.B or ElliottPosition.X or ElliottPosition.CombinationEnd
            ? ["Zigzag","Flat","ExpandedFlat","RunningFlat","ContractingTriangle","ExpandingTriangle","DoubleThree","TripleThree"]
            : ["Zigzag","Flat","ExpandedFlat","RunningFlat","DoubleThree","TripleThree"],
        _ => [role]
    };

    private static (string,ElliottPosition)[] ChildRoles(string pattern, int count)
    {
        if (pattern is "DoubleThree" or "TripleThree")
            return Enumerable.Range(0,count).Select(i=>("Correction",i%2==1 ? ElliottPosition.X
                : i==count-1 ? ElliottPosition.CombinationEnd : ElliottPosition.Wave2)).ToArray();
        if (pattern == "Zigzag") return [("Motive",ElliottPosition.A),("Correction",ElliottPosition.B),("Motive",ElliottPosition.C)];
        if (pattern is "Flat" or "ExpandedFlat" or "RunningFlat") return [("Correction",ElliottPosition.A),("Correction",ElliottPosition.B),("Motive",ElliottPosition.C)];
        if (pattern.EndsWith("Triangle")) return Enumerable.Repeat(("Correction",ElliottPosition.Unknown),count).ToArray();
        return Enumerable.Range(0,count).Select(i => (pattern == "EndingDiagonal" ? "Zigzag"
            : i%2==0 ? "Motive" : "Correction", (ElliottPosition)(i+1))).ToArray();
    }

    public static bool NeedsPosition(string pattern) => pattern.EndsWith("Diagonal") || pattern.EndsWith("Triangle");
    public static bool PlacementAllowed(string pattern, ElliottPosition position) => position == ElliottPosition.Unknown || pattern switch
    {
        "EndingDiagonal" => position is ElliottPosition.Wave5 or ElliottPosition.C,
        "LeadingDiagonal" => position is ElliottPosition.Wave1 or ElliottPosition.A,
        "ContractingTriangle" or "ExpandingTriangle" => position is ElliottPosition.Wave4 or ElliottPosition.B or ElliottPosition.X or ElliottPosition.CombinationEnd,
        _ => true
    };

    public static bool Geometry(string pattern, IReadOnlyList<ElliottPivot> p)
    {
        if (p.Count < 2 || p.Zip(p.Skip(1)).Any(x => x.First.Time >= x.Second.Time || x.First.Price == x.Second.Price || x.First.Kind == x.Second.Kind || (x.First.Kind == "Low") != (x.Second.Price > x.First.Price))) return false;
        var bull = p[1].Price > p[0].Price;
        decimal Q(int i) => Directed(p[i].Price-p[0].Price,bull);
        decimal L(int i) => Math.Abs(p[i+1].Price-p[i].Price);
        if (pattern is "Impulse" or "TruncatedImpulse" or "HarmonicImpulse")
            return p.Count == 6 && Q(2)>0 && Q(3)>Q(1) && Q(4)>Q(2)
                && (pattern == "HarmonicImpulse" ? Q(5)>Q(3) : Q(4)>Q(1) && L(2)>=Math.Min(L(0),L(4)));
        if (pattern is "LeadingDiagonal" or "EndingDiagonal")
        {
            if (p.Count != 6 || Q(2)<=0 || Q(3)<=Q(1) || Q(4)<=Q(2)) return false;
            var contracting=L(2)<L(0) && L(4)<L(2) && L(3)<L(1);
            var expanding=L(2)>L(0) && L(4)>L(2) && L(3)>L(1) && Q(5)>Q(3);
            // Compare trend-boundary widths at the SAME times, not only leg lengths.
            decimal Upper(long t) => Q(1)+(Q(3)-Q(1))*(t-p[1].Time)/(p[3].Time-p[1].Time);
            decimal Lower(long t) => Q(2)+(Q(4)-Q(2))*(t-p[2].Time)/(p[4].Time-p[2].Time);
            var w1=Upper(p[2].Time)-Lower(p[2].Time); var w2=Upper(p[4].Time)-Lower(p[4].Time);
            return w1>0 && w2>0 && (contracting && w2<w1 || expanding && w2>w1);
        }
        if (pattern.EndsWith("Triangle"))
            return p.Count==6 && (pattern=="ContractingTriangle"
                ? Q(3)<Q(1) && Q(4)>Q(2) && Q(5)<Q(3) && Q(5)>Q(4) && L(2)<L(0) && L(3)<L(1) && L(4)<L(2)
                : Q(3)>Q(1) && Q(4)<Q(2) && Q(5)>Q(3) && L(2)>L(0) && L(3)>L(1) && L(4)>L(2));
        if (pattern is "DoubleThree" or "TripleThree") return p.Count == (pattern == "DoubleThree" ? 4 : 6);
        if (p.Count!=4) return false;
        return pattern switch
        {
            "Zigzag" => Q(2)>0 && Q(2)<Q(1) && Q(3)>Q(1),
            // B-depth is a ranking guideline, not a substitute for 3-3-5.
            "Flat" => Q(2)>=0 && Q(2)<Q(1),
            "ExpandedFlat" => Q(2)<0 && Q(3)>Q(1),
            "RunningFlat" => Q(2)<0 && Q(3)<=Q(1),
            _ => false
        };
    }
    private static decimal Directed(decimal value,bool bull) => bull ? value : -value;

    private static void AddLabels(ElliottStructureNode node,string parent,int degree,List<ElliottWavePoint> output,IReadOnlyList<ElliottPivot> raw)
    {
        if (node.Children.Count==0) return;
        var numeric = node.Pattern is "Impulse" or "LeadingDiagonal" or "EndingDiagonal";
        string[]? combination = node.Pattern == "DoubleThree" ? ["W","X","Y"] : node.Pattern == "TripleThree" ? ["W","X","Y","X","Z"] : null;
        for(var i=0;i<node.Children.Count;i++)
        {
            var child=node.Children[i]; var pivot=raw.First(p=>p.Time==child.End);
            var label=combination is not null ? combination[i] : numeric ? (i+1).ToString() : ((char)('A'+i)).ToString();
            output.Add(new(label,pivot.Time,pivot.Price,degree,parent));
            AddLabels(child,$"{parent}/{label}",degree+1,output,raw);
        }
    }
}

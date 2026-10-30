namespace Phoenix.Web;

// Only frozen support/resistance observations enter this metric. Outcome labels,
// cloud/Fibonacci, volatility regime and trade reward/risk never enter its score.
internal sealed record TechnicalResemblance(double Score, bool Comparable,
    IReadOnlyDictionary<string, double> Distances)
{
    internal const int Version = 6;
    internal const string Metric = "support-resistance-neighborhood-v6";
    internal bool Nearby => Comparable && Score >= Math.Exp(-.5d) &&
        Distances.TryGetValue("support-resistance", out var entry) && entry <= 1.25d &&
        Distances.Where(x => x.Key is "stop-level" or "target-level" or "entry-strength").All(x => x.Value <= 1.5d);

    internal static TechnicalResemblance Compare(ServerSignal a, ServerSignal b,
        TechnicalFeatureSnapshot x, TechnicalFeatureSnapshot y)
    {
        var groups = new Dictionary<string, double>();
        var weights = new Dictionary<string, double>();
        var mirrored = a.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase) !=
            b.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var measuredEntry = 0;
        var entry = new List<double>();
        var stop = new List<double>();
        var target = new List<double>();
        var entryStrength = new List<double>(); var stopStrength = new List<double>(); var targetStrength = new List<double>();
        if (x.LevelContext is { } left && y.LevelContext is { } right)
        {
            measuredEntry = Level(left.Entry, right.Entry, entry, entryStrength);
            Level(left.Stop, right.Stop, stop, stopStrength);
            Level(left.Target, right.Target, target, targetStrength);
        }
        else
        {
            // Saturated legacy readings remain unknown, rather than invented zeros.
            measuredEntry += Distance(x.SupportDistanceAtr,
                mirrored ? y.ResistanceDistanceAtr : y.SupportDistanceAtr, entry, clipped: true);
            measuredEntry += Distance(x.ResistanceDistanceAtr,
                mirrored ? y.SupportDistanceAtr : y.ResistanceDistanceAtr, entry, clipped: true);
        }
        Pair(x.EntryLevelStrength, y.EntryLevelStrength, entryStrength);
        Pair(x.StopLevelStrength, y.StopLevelStrength, stopStrength);
        Pair(x.TargetLevelStrength, y.TargetLevelStrength, targetStrength);
        Add("support-resistance", entry, 4);
        Add("entry-strength", entryStrength, 2); // Entry is the most important level.
        Add("stop-level", stop, 1); Add("stop-strength", stopStrength, 1);
        Add("target-level", target, 1); Add("target-strength", targetStrength, 1);
        var multi = new List<double>();
        if (x.MultiScaleLevels is { } mx && y.MultiScaleLevels is { } my)
        {
            foreach (var pair in mx.Join(my, l => (l.Interval, l.Window), r => (r.Interval, r.Window),
                (l, r) => (Left: l, Right: r)))
            {
                var l = pair.Left; var r = pair.Right;
                var scaleEntry = new List<double>(); var scaleStop = new List<double>(); var scaleTarget = new List<double>();
                if (l.LevelContext is { } lc && r.LevelContext is { } rc)
                {
                    Level(lc.Entry, rc.Entry, scaleEntry, scaleEntry);
                    Level(lc.Stop, rc.Stop, scaleStop, scaleStop);
                    Level(lc.Target, rc.Target, scaleTarget, scaleTarget);
                }
                Pair(l.EntryStrength, r.EntryStrength, scaleEntry);
                Pair(l.StopStrength, r.StopStrength, scaleStop);
                Pair(l.TargetStrength, r.TargetStrength, scaleTarget);
                // Each named timeframe/window retains its identity; entry has 3x weight.
                multi.AddRange(scaleEntry); multi.AddRange(scaleEntry); multi.AddRange(scaleEntry);
                multi.AddRange(scaleStop); multi.AddRange(scaleTarget);
            }
        }
        Add("multiscale-levels", multi, 2);
        var comparable = measuredEntry > 0 && x.AtrPercent > 0 && y.AtrPercent > 0;
        var squared = groups.Count == 0 ? double.PositiveInfinity :
            groups.Sum(g => g.Value * g.Value * weights[g.Key]) / weights.Values.Sum();
        return new(comparable ? Math.Exp(-.5d * squared) : 0, comparable, groups);

        int Level(TechnicalLevelGeometry l, TechnicalLevelGeometry r, List<double> values, List<double> strength)
        {
            var measured = Distance(l.SupportDistanceAtr,
                mirrored ? r.ResistanceDistanceAtr : r.SupportDistanceAtr, values);
            measured += Distance(l.ResistanceDistanceAtr,
                mirrored ? r.SupportDistanceAtr : r.ResistanceDistanceAtr, values);
            if (measured == 0) return 0;
            var touches = new List<double> { CountDistance(l.NearbyPivots, r.NearbyPivots) };
            CountPair(l.SupportTouches, mirrored ? r.ResistanceTouches : r.SupportTouches, touches);
            CountPair(l.ResistanceTouches, mirrored ? r.SupportTouches : r.ResistanceTouches, touches);
            strength.Add(Math.Sqrt(touches.Average(v => v * v)));
            return measured;
        }
        void Add(string name, IReadOnlyList<double> values, double weight)
        {
            if (values.Count == 0) return;
            groups[name] = Math.Sqrt(values.Average(v => v * v)); weights[name] = weight;
        }
    }

    // One unit means a factor of two in (1 + distance in ATR), or four in
    // historical pivot touches. Fixed units never adapt to an outcome's size.
    private static int Distance(decimal? a, decimal? b, List<double> values, bool clipped = false)
    {
        if (a is not >= 0 || b is not >= 0 || clipped && a >= 10 && b >= 10) return 0;
        values.Add(Math.Log((1d + (double)a.Value) / (1d + (double)b.Value)) / Math.Log(2d));
        return 1;
    }
    private static double CountDistance(int a, int b) => Math.Log((1d + Math.Max(0, a)) /
        (1d + Math.Max(0, b))) / Math.Log(4d);
    private static void CountPair(int? a, int? b, List<double> values)
    { if (a.HasValue && b.HasValue) values.Add(CountDistance(a.Value, b.Value)); }
    private static void Pair(decimal? a, decimal? b, List<double> values)
    { if (a.HasValue && b.HasValue) values.Add((double)((a.Value - b.Value) / .5m)); }
}

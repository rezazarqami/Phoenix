namespace Phoenix.Web;

// Distances use fixed, versioned technical units, not archive dispersion or outcome labels.
// A radius of one means the RMS mismatch fits the documented neighborhood.
internal sealed record TechnicalResemblance(double Score, bool Comparable,
    IReadOnlyDictionary<string, double> Distances)
{
    internal const string Metric = "technical-neighborhood-v3";
    internal bool Nearby => Comparable && Distances.Values.All(distance => distance <= 1d);

    internal static TechnicalResemblance Compare(ServerSignal a, ServerSignal b,
        TechnicalFeatureSnapshot x, TechnicalFeatureSnapshot y)
    {
        var groups = new Dictionary<string, double>();
        var weights = new Dictionary<string, double>();
        var sign = a.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
        var otherSign = b.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
        var support = new List<double>();
        // log(1+ATR-distance) keeps both close levels and distant levels distinguishable.
        // Two capped values (10 ATR) say only "far away", not how far: no match credit.
        Distance(x.SupportDistanceAtr, sign==otherSign ? y.SupportDistanceAtr : y.ResistanceDistanceAtr);
        Distance(x.ResistanceDistanceAtr, sign==otherSign ? y.ResistanceDistanceAtr : y.SupportDistanceAtr);
        var measuredDistances=support.Count;
        Pair(x.EntryLevelStrength, y.EntryLevelStrength, .25m, support);
        Pair(x.StopLevelStrength, y.StopLevelStrength, .25m, support);
        Pair(x.TargetLevelStrength, y.TargetLevelStrength, .25m, support);
        Add("support-resistance", support, 4);
        var clouds = new List<double> {
            Difference((x.IchimokuEntryPosition ?? x.IchimokuPosition)*sign,
                (y.IchimokuEntryPosition ?? y.IchimokuPosition)*otherSign, .35m),
            Difference(x.IchimokuCloudBias*sign,y.IchimokuCloudBias*otherSign,.35m) };
        Pair(x.IchimokuStopPosition*sign,y.IchimokuStopPosition*otherSign,.35m,clouds);
        Pair(x.IchimokuTargetPosition*sign,y.IchimokuTargetPosition*otherSign,.35m,clouds);
        Add("ichimoku", clouds, 2);
        if(x.LevelContext is { } lx && y.LevelContext is { } ly)
        {
            // New snapshots retain uncapped distances at entry, target AND stop.
            // Legacy clipped readings stay legacy; no reconstruction from future candles.
            var geometry=new List<double>();var rawClouds=new List<double>();
            CompareLevel(lx.Entry,ly.Entry,geometry,rawClouds);
            CompareLevel(lx.Stop,ly.Stop,geometry,rawClouds);
            CompareLevel(lx.Target,ly.Target,geometry,rawClouds);
            measuredDistances+=new[]{(lx.Entry,ly.Entry),(lx.Stop,ly.Stop),(lx.Target,ly.Target)}.Sum(pair=>
                (pair.Item1.SupportDistanceAtr.HasValue && pair.Item2.SupportDistanceAtr.HasValue?1:0)+
                (pair.Item1.ResistanceDistanceAtr.HasValue && pair.Item2.ResistanceDistanceAtr.HasValue?1:0));
            rawClouds.Add(Difference(x.IchimokuCloudBias*sign,y.IchimokuCloudBias*otherSign,.35m));
            Add("level-geometry",geometry,4);
            Add("ichimoku",rawClouds,2);
        }
        if(x.MultiScaleLevels is {Count:>0} && y.MultiScaleLevels is {Count:>0})
        {
            var levels=new List<double>();var fib=new List<double>();var contextCloud=new List<double>();
            // Match the same actual timeframe AND history window, never positional averages.
            foreach(var pair in x.MultiScaleLevels.Join(y.MultiScaleLevels,
                v=>(v.Interval,v.Window),v=>(v.Interval,v.Window),(l,r)=>(Left:l,Right:r)))
            {
                var l=pair.Left;var r=pair.Right;
                if(l.LevelContext is { } lc && r.LevelContext is { } rc)
                {
                    var rawLevels=new List<double>();
                    CompareLevel(lc.Entry,rc.Entry,rawLevels,contextCloud);
                    CompareLevel(lc.Stop,rc.Stop,rawLevels,contextCloud);
                    CompareLevel(lc.Target,rc.Target,rawLevels,contextCloud);
                    levels.AddRange(rawLevels);
                }
                levels.AddRange([Difference(l.EntryStrength,r.EntryStrength,.25m),
                    Difference(l.StopStrength,r.StopStrength,.25m),Difference(l.TargetStrength,r.TargetStrength,.25m)]);
                fib.AddRange([Difference(l.FibonacciEntry,r.FibonacciEntry,.25m),
                    Difference(l.FibonacciStop,r.FibonacciStop,.25m),Difference(l.FibonacciTarget,r.FibonacciTarget,.25m)]);
                contextCloud.AddRange([Difference(l.CloudEntry*sign,r.CloudEntry*otherSign,.35m),
                    Difference(l.CloudStop*sign,r.CloudStop*otherSign,.35m),
                    Difference(l.CloudTarget*sign,r.CloudTarget*otherSign,.35m),
                    Difference(l.CloudBias*sign,r.CloudBias*otherSign,.35m)]);
            }
            Add("multiscale-levels",levels,2);Add("fibonacci",fib,2);Add("multiscale-cloud",contextCloud,1);
        }
        if(x.TradeGeometry is {StopDistanceAtr:>0,TargetDistanceAtr:>0} gx &&
           y.TradeGeometry is {StopDistanceAtr:>0,TargetDistanceAtr:>0} gy)
            Add("trade-geometry",[Ratio(gx.StopDistanceAtr,gy.StopDistanceAtr),
                Ratio(gx.TargetDistanceAtr,gy.TargetDistanceAtr)],2);
        var structure=new List<double>();
        if(x.SimilarityContextVersion>=1 && y.SimilarityContextVersion>=1)
        {
            structure.Add(Difference(x.MarketRegimeScore,y.MarketRegimeScore,.25m));
            structure.Add(Difference(x.StructureScore,y.StructureScore,.25m));
            structure.Add(Difference(x.LiquiditySweepScore,y.LiquiditySweepScore,.25m));
        }
        Pair(x.StructureBreakScore,y.StructureBreakScore,.25m,structure);
        Pair(x.StructureRetestScore,y.StructureRetestScore,.25m,structure);
        Add("structure",structure,1);
        // Volatility is compared relatively. RSI, volume, BTC alignment, current entry
        // distance and the result label never enter this ruler.
        if(x.AtrPercent>0 && y.AtrPercent>0)Add("volatility",[Ratio(x.AtrPercent,y.AtrPercent)],1);
        var comparable=measuredDistances>0 && support.Count>=2 && x.AtrPercent>0 && y.AtrPercent>0;
        var distance=groups.Count==0?double.PositiveInfinity:
            groups.Sum(g=>g.Value*g.Value*weights[g.Key])/weights.Values.Sum();
        return new(comparable?Math.Exp(-.5d*distance):0d,comparable,groups);

        void CompareLevel(TechnicalLevelGeometry l,TechnicalLevelGeometry r,List<double> levels,List<double> cloud)
        {
            var rightSupport=sign==otherSign?r.SupportDistanceAtr:r.ResistanceDistanceAtr;
            var rightResistance=sign==otherSign?r.ResistanceDistanceAtr:r.SupportDistanceAtr;
            if(l.SupportDistanceAtr.HasValue && rightSupport.HasValue)
                levels.Add(Math.Log((1d+(double)l.SupportDistanceAtr)/(1d+(double)rightSupport))/Math.Log(1.5d));
            if(l.ResistanceDistanceAtr.HasValue && rightResistance.HasValue)
                levels.Add(Math.Log((1d+(double)l.ResistanceDistanceAtr)/(1d+(double)rightResistance))/Math.Log(1.5d));
            levels.Add(Math.Log((1d+l.NearbyPivots)/(1d+r.NearbyPivots))/Math.Log(2d));
            cloud.Add(Difference(l.CloudDistanceAtr*sign,r.CloudDistanceAtr*otherSign,1m));
        }
        void Distance(decimal left,decimal right)
        {
            if(left<0 || right<0 || left>=10m && right>=10m)return;
            support.Add(Math.Log((1d+(double)left)/(1d+(double)right))/Math.Log(1.5d));
        }
        void Add(string name,IReadOnlyList<double> readings,double weight)
        {
            if(readings.Count==0)return;
            groups[name]=Math.Sqrt(readings.Average(v=>v*v));weights[name]=weight;
        }
    }
    private static double Ratio(decimal a,decimal b)=>Math.Log((double)a/(double)b)/Math.Log(2d);
    private static double Difference(decimal a,decimal b,decimal unit)=>(double)((a-b)/unit);
    private static void Pair(decimal? a,decimal? b,decimal unit,List<double> result)
    {if(a.HasValue && b.HasValue)result.Add(Difference(a.Value,b.Value,unit));}
}

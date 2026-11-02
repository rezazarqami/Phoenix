namespace Phoenix.Web;

internal sealed record TechnicalResemblance(double Score, bool Comparable,
    IReadOnlyDictionary<string,double> Distances)
{
    internal const int Version = 10;
    internal const string Metric = "scale-independent-bounded-wave-plan-v10";
    private static readonly string[] PlanKeys=["entry-position","target-position","stop-position","risk-ratio","reward-risk"];
    private static readonly string[] BodyKeys=["efficiency","directional-bodies","body-fraction","overlap","countermove","shape"];
    private static readonly string[] EntryKeys=["entry-occupancy","entry-crossing-power","entry-rejections"];
    private double Group(string[] keys)=>Math.Sqrt(keys.Average(k=>Distances[k]*Distances[k]));
    internal bool Nearby => Comparable && PlanKeys.All(k=>Distances[k]<=1) &&
        Distances["ordered-path"]<=1 && Distances["path-maximum"]<=1 &&
        Group(BodyKeys)<=1 && Group(EntryKeys)<=1;

    internal IEnumerable<string> Rejections()
    {
        if(!Comparable){yield return "insufficient-or-incompatible";yield break;}
        if(PlanKeys.Any(k=>Distances[k]>1))yield return "trade-plan";
        if(Distances["ordered-path"]>1 || Distances["path-maximum"]>1)yield return "ordered-wave";
        if(Group(BodyKeys)>1)yield return "wave-body-group";
        if(Group(EntryKeys)>1)yield return "entry-interaction-group";
    }

    // Sakoe-Chiba band: at most four of 32 price-progress samples may shift.
    // Direction/order is preserved; there is no unconstrained stretching or outcome input.
    internal static (double Rms,double Maximum) Align(decimal[] left,decimal[] right)
    {
        const int n=32;const int radius=4;
        var cost=new double[n+1,n+1];var peak=new double[n+1,n+1];
        for(var i=0;i<=n;i++)for(var j=0;j<=n;j++)cost[i,j]=double.PositiveInfinity;
        cost[0,0]=0;
        for(var i=1;i<=n;i++)for(var j=Math.Max(1,i-radius);j<=Math.Min(n,i+radius);j++)
        {
            var previous=cost[i-1,j-1];var maximum=peak[i-1,j-1];
            if(cost[i-1,j]<previous){previous=cost[i-1,j];maximum=peak[i-1,j];}
            if(cost[i,j-1]<previous){previous=cost[i,j-1];maximum=peak[i,j-1];}
            var difference=(double)Math.Abs(left[i-1]-right[j-1]);
            cost[i,j]=previous+difference*difference;peak[i,j]=Math.Max(maximum,difference);
        }
        // Fixed denominator avoids rewarding extra alignment steps with a lower average.
        return (Math.Sqrt(cost[n,n]/n),peak[n,n]);
    }

    internal static TechnicalResemblance Compare(ServerSignal a, ServerSignal b,
        TechnicalFeatureSnapshot x, TechnicalFeatureSnapshot y, bool allowOtherTimeframes = false)
    {
        var d=new Dictionary<string,double>();
        if(x.Wave is not {Version:WaveFingerprint.CurrentVersion} l ||
           y.Wave is not {Version:WaveFingerprint.CurrentVersion} r || l.Shape is not {Length:8} || r.Shape is not {Length:8})
            return new(0,false,d);
        if (!a.Direction.Equals(b.Direction,StringComparison.OrdinalIgnoreCase) ||
            !(a.Direction.Equals("Long",StringComparison.OrdinalIgnoreCase)||a.Direction.Equals("Short",StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(a.Timeframe) || string.IsNullOrWhiteSpace(b.Timeframe) ||
            (!allowOtherTimeframes && a.Timeframe!=b.Timeframe) || l.Rising!=r.Rising ||
            l.OrderedPath is not {Length:32} || r.OrderedPath is not {Length:32}) return new(0,false,d);
        static decimal Position(decimal price,WaveFingerprint w)
        {
            var p=(decimal)(Math.Log((double)(price/w.Floor))/Math.Log((double)(w.Ceiling/w.Floor)));
            return w.Rising?p:1-p;
        }
        static bool Valid(ServerSignal s,WaveFingerprint w) => w.Floor>0 && w.Ceiling>w.Floor &&
            s.EntryPrice>0 && s.TakeProfit>0 && s.StopLoss>0 &&
            (s.Direction.Equals("Long",StringComparison.OrdinalIgnoreCase)
                ? s.StopLoss<s.EntryPrice && s.TakeProfit>s.EntryPrice
                : s.StopLoss>s.EntryPrice && s.TakeProfit<s.EntryPrice);
        if(!Valid(a,l)||!Valid(b,r))return new(0,false,d);
        void Add(string key,decimal left,decimal right,decimal tolerance) => d[key]=(double)(Math.Abs(left-right)/tolerance);
        Add("efficiency",l.Efficiency,r.Efficiency,.15m);
        Add("directional-bodies",l.DirectionalBodies,r.DirectionalBodies,.2m);
        Add("body-fraction",l.BodyFraction,r.BodyFraction,.2m);
        Add("overlap",l.Overlap,r.Overlap,.2m);
        Add("countermove",l.Countermove,r.Countermove,.12m);
        Add("entry-occupancy",l.EntryOccupancy,r.EntryOccupancy,.15m);
        Add("entry-crossing-power",l.EntryCrossingPower,r.EntryCrossingPower,.25m);
        Add("entry-rejections",l.EntryRejections,r.EntryRejections,.15m);
        d["shape"]=Math.Sqrt(l.Shape.Zip(r.Shape,(v,w)=>Math.Pow((double)((v-w)/.15m),2)).Average());
        Add("entry-position",Position(a.EntryPrice,l),Position(b.EntryPrice,r),.04m);
        Add("target-position",Position(a.TakeProfit,l),Position(b.TakeProfit,r),.05m);
        Add("stop-position",Position(a.StopLoss,l),Position(b.StopLoss,r),.04m);
        var riskA=Math.Abs(Position(a.EntryPrice,l)-Position(a.StopLoss,l));
        var riskB=Math.Abs(Position(b.EntryPrice,r)-Position(b.StopLoss,r));
        var rewardA=Math.Abs(Position(a.TakeProfit,l)-Position(a.EntryPrice,l));
        var rewardB=Math.Abs(Position(b.TakeProfit,r)-Position(b.EntryPrice,r));
        if(riskA<=0||riskB<=0||rewardA<=0||rewardB<=0)return new(0,false,d);
        d["risk-ratio"]=Math.Abs(Math.Log((double)(riskA/riskB)))/Math.Log(1.25);
        d["reward-risk"]=Math.Abs(Math.Log((double)((rewardA/riskA)/(rewardB/riskB))))/Math.Log(1.25);
        var path=Align(l.OrderedPath,r.OrderedPath);
        d["ordered-path"]=path.Rms/.08;
        d["path-maximum"]=path.Maximum/.18;
        return new(Math.Exp(-.5d*d.Values.Average(v=>v*v)),true,d);
    }
}

namespace Phoenix.Web;

internal sealed record TechnicalResemblance(double Score, bool Comparable,
    IReadOnlyDictionary<string,double> Distances)
{
    internal const int Version = 8;
    internal const string Metric = "anchored-wave-plan-v8";
    internal bool Nearby => Comparable && Distances.Values.All(x=>x<=1d);

    internal static TechnicalResemblance Compare(ServerSignal a, ServerSignal b,
        TechnicalFeatureSnapshot x, TechnicalFeatureSnapshot y)
    {
        var d=new Dictionary<string,double>();
        if(x.Wave is not {Version:WaveFingerprint.CurrentVersion} l ||
           y.Wave is not {Version:WaveFingerprint.CurrentVersion} r || l.Shape is not {Length:8} || r.Shape is not {Length:8})
            return new(0,false,d);
        if (!a.Direction.Equals(b.Direction,StringComparison.OrdinalIgnoreCase) ||
            !(a.Direction.Equals("Long",StringComparison.OrdinalIgnoreCase)||a.Direction.Equals("Short",StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(a.Timeframe) || a.Timeframe!=b.Timeframe || l.Rising!=r.Rising ||
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
        var path=l.OrderedPath.Zip(r.OrderedPath,(v,w)=>(double)Math.Abs(v-w)).ToArray();
        d["ordered-path"]=Math.Sqrt(path.Average(v=>v*v))/.08;
        d["path-maximum"]=path.Max()/.18;
        return new(Math.Exp(-.5d*d.Values.Average(v=>v*v)),true,d);
    }
}

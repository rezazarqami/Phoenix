namespace Phoenix.Web;

internal sealed record TechnicalResemblance(double Score, bool Comparable,
    IReadOnlyDictionary<string,double> Distances)
{
    internal const int Version = 7;
    internal const string Metric = "anchored-wave-entry-structure-v7";
    internal bool Nearby => Comparable && Distances.Values.All(x=>x<=1d);

    internal static TechnicalResemblance Compare(ServerSignal a, ServerSignal b,
        TechnicalFeatureSnapshot x, TechnicalFeatureSnapshot y)
    {
        var d=new Dictionary<string,double>();
        if(x.Wave is not {Version:WaveFingerprint.CurrentVersion} l ||
           y.Wave is not {Version:WaveFingerprint.CurrentVersion} r || l.Shape is not {Length:8} || r.Shape is not {Length:8})
            return new(0,false,d);
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
        return new(Math.Exp(-.5d*d.Values.Average(v=>v*v)),true,d);
    }
}

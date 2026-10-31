using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

// Dimensionless price structure only. No elapsed time, Elliott labels or outcome inputs.
public sealed record WaveFingerprint(int Version, long Start, long End, decimal Floor, decimal Ceiling,
    decimal Efficiency, decimal DirectionalBodies, decimal BodyFraction, decimal Overlap,
    decimal Countermove, decimal EntryOccupancy, decimal EntryCrossingPower,
    decimal EntryRejections, decimal[] Shape)
{
    public const int CurrentVersion = 1;

    public static WaveFingerprint? Extract(IReadOnlyList<BybitKline> source, SignalCandidate c)
    {
        var start = Math.Min(c.CeilingTime, c.FloorTime);
        var end = Math.Max(c.CeilingTime, c.FloorTime);
        var bars = source.Where(x => x.OpenTime >= start && x.OpenTime <= end).OrderBy(x => x.OpenTime).ToArray();
        // Never invent anchors from the first/last candle of an unrelated history.
        if (bars.Length < 3 || bars[0].OpenTime != start || bars[^1].OpenTime != end ||
            c.EntryPrice <= 0 || c.Floor <= 0 || c.Ceiling <= c.Floor || bars.Any(x => x.Low <= 0)) return null;
        var up = c.FloorTime < c.CeilingTime;
        var anchorTolerance = (c.Ceiling - c.Floor) * .001m;
        bool Anchor(BybitKline b, decimal price) => Math.Abs(b.High-price)<=anchorTolerance ||
            Math.Abs(b.Low-price)<=anchorTolerance || Math.Abs(b.Close-price)<=anchorTolerance;
        if (!Anchor(bars[0], up ? c.Floor : c.Ceiling) || !Anchor(bars[^1], up ? c.Ceiling : c.Floor)) return null;
        var logLow = Math.Log((double)c.Floor);
        var logRange = Math.Log((double)c.Ceiling) - logLow;
        decimal P(decimal price) => (decimal)((Math.Log((double)price)-logLow)/logRange);
        decimal Oriented(decimal price) => up ? P(price) : 1-P(price);
        decimal Clamp(decimal v) => Math.Clamp(v,0m,1m);
        var path = 0m; var counter = 0m; var extreme = 0m;
        var overlap = 0m; var crossings = new List<decimal>();
        var entry = P(c.EntryPrice); const decimal band = .035m;
        var occupancy = 0m; var rejections = 0m;
        for (var i=0;i<bars.Length;i++)
        {
            var b=bars[i]; var lo=P(b.Low); var hi=P(b.High);
            var bodyLo=P(Math.Min(b.Open,b.Close)); var bodyHi=P(Math.Max(b.Open,b.Close));
            if (hi>lo) occupancy += Math.Max(0,Math.Min(hi,entry+band)-Math.Max(lo,entry-band))/(hi-lo);
            if (bodyLo <= entry && bodyHi >= entry)
                crossings.Add(Clamp((bodyHi-bodyLo)/.2m) * (hi>lo ? (bodyHi-bodyLo)/(hi-lo):0));
            else if (lo<=entry && hi>=entry) rejections++;
            if(i==0) { extreme=Oriented(b.Close); continue; }
            var previous=bars[i-1];
            path+=Math.Abs(Oriented(b.Close)-Oriented(previous.Close));
            extreme=Math.Max(extreme,Oriented(b.Close));
            counter=Math.Max(counter,extreme-Oriented(b.Close));
            var union=Math.Max(b.High,previous.High)-Math.Min(b.Low,previous.Low);
            overlap+=union>0 ? Math.Max(0,Math.Min(b.High,previous.High)-Math.Max(b.Low,previous.Low))/union:0;
        }
        // Price-progress bins preserve where internal corrections occur without comparing duration.
        var shape=new decimal[8];
        for(var bin=0;bin<shape.Length;bin++)
        {
            var level=(bin+.5m)/shape.Length;
            var selected=bars.Where(b=>Oriented(b.Close)>=level-.125m && Oriented(b.Close)<=level+.125m).ToArray();
            shape[bin]=selected.Length==0?0:Clamp(selected.Average(b=>Math.Abs(P(b.Close)-P(b.Open))));
        }
        return new(CurrentVersion,start,end,c.Floor,c.Ceiling,
            path>0?Clamp(Math.Abs(Oriented(bars[^1].Close)-Oriented(bars[0].Close))/path):0,
            bars.Count(b=>up?b.Close>b.Open:b.Close<b.Open)/(decimal)bars.Length,
            bars.Average(b=>b.High>b.Low?Math.Abs(b.Close-b.Open)/(b.High-b.Low):0),
            overlap/(bars.Length-1),Clamp(counter),occupancy/bars.Length,
            crossings.Count>0?crossings.Max():0,rejections/bars.Length,shape);
    }
}

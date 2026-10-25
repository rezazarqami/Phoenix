using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;

public static class ElliottRulesTests
{
    public static void RunAll(Action<string,Action> run)
    {
        ElliottCoverageTests.RunAll(run);
        run("Elliott PDF confirms a full 5-3-5-3-5 with exact parent boundaries",()=>{
            var (s,raw)=Fixture("Impulse",[100,120,110,150,130,160]);
            var r=new ElliottStructureValidator().Validate(s,raw);
            Check(r.ValidationStatus=="Verified" && r.Phase=="Complete");
            Check(r.SubdivisionCoveragePercent==100 && r.Structure!.Children.Count==5);
            Check(r.Structure!.Children[0].Start==s.Waves[0].Time && r.Structure.Children[^1].End==s.Waves[^1].Time);
        });
        run("Elliott PDF rejects a three as an impulse child without claiming completion",()=>{
            var (s,raw)=Fixture("Impulse",[100,120,110,150,130,160],firstAsCorrection:true);
            var r=new ElliottStructureValidator().Validate(s,raw);
            Check(r.ValidationStatus=="Unverified" && r.Phase!="Complete");
            Check(r.Rules.Any(x=>x.Code=="subdivision-0-1" && x.Status=="Unknown"));
        });
        run("Elliott PDF does not confirm the live final leg",()=>{
            var (s,raw)=Fixture("Impulse",[100,120,110,150,130,160]);
            var r=new ElliottStructureValidator().Validate(s,raw[..^1]);
            Check(r.Phase=="Developing" && r.Waves[^1].IsTentative);
            Check(r.Waves.Take(r.Waves.Count-1).All(w=>!w.IsTentative));
        });
        run("Elliott PDF rejects origin breaches hidden inside the second wave",()=>{
            var (s,raw)=Fixture("Impulse",[100,120,110,150,130,160]);
            var index=Array.FindIndex(raw,p=>p.Time>s.Waves[1].Time && p.Time<s.Waves[2].Time);
            raw[index]=raw[index] with { Price=90 };
            Check(new ElliottStructureValidator().Validate(s,raw).ValidationStatus=="Invalid");
        });
        run("Elliott PDF requires observed fifth subdivisions for truncation",()=>{
            var (s,raw)=Fixture("TruncatedImpulse",[100,120,110,150,130,145]);
            Check(new ElliottStructureValidator().Validate(s,raw).ValidationStatus=="Verified");
            var fifthStart=s.Waves[4].Time;
            raw=raw.Where(p=>p.Time<=fifthStart || p.Time>=s.Waves[5].Time).ToArray();
            Check(new ElliottStructureValidator().Validate(s,raw).Phase!="Complete");
        });
        run("Elliott PDF a shorter third is allowed but the shortest third is not",()=>{
            Check(Geometry("Impulse",[100,130,120,145,135,155]));
            Check(!Geometry("Impulse",[100,130,120,135,131,170]));
        });
        run("Elliott PDF equal origin and wave-one contact use explicit strict policy",()=>{
            Check(!Geometry("Impulse",[100,120,100,150,130,160]));
            Check(!Geometry("Impulse",[100,120,110,150,120,160]));
        });
        run("Elliott PDF overlap alone is not a diagonal",()=>{
            Check(!Geometry("Impulse",[100,120,110,145,115,155]));
            Check(!Geometry("EndingDiagonal",[100,120,110,145,115,150]));
        });
        run("Elliott PDF both diagonal geometries and parent positions are enforced",()=>{
            Check(Geometry("EndingDiagonal",[100,120,110,125,117,127]));
            Check(Geometry("EndingDiagonal",[100,110,105,120,108,130]));
            Check(!Geometry("EndingDiagonal",[100,110,105,120,108,119]));
            Check(!ElliottStructureValidator.PlacementAllowed("EndingDiagonal",ElliottPosition.Wave3));
            Check(ElliottStructureValidator.PlacementAllowed("LeadingDiagonal",ElliottPosition.A));
        });
        run("Elliott PDF triangle needs a parent and is not a standalone second wave",()=>{
            var (s,raw)=Fixture("ContractingTriangle",[100,120,105,117,108,115]);
            Check(new ElliottStructureValidator().Validate(s,raw).ValidationStatus=="Unverified");
            Check(new ElliottStructureValidator().Validate(s,raw,ElliottPosition.Wave2).ValidationStatus=="Invalid");
            Check(new ElliottStructureValidator().Validate(s,raw,ElliottPosition.Wave4).ValidationStatus=="Verified");
        });
        run("Elliott PDF zigzag and flat use different A subdivisions",()=>{
            var (zig,raw)=Fixture("Zigzag",[150,120,135,110]);
            Check(new ElliottStructureValidator().Validate(zig,raw).ValidationStatus=="Verified");
            Check(new ElliottStructureValidator().Validate(zig with { Pattern="Flat" },raw).ValidationStatus!="Verified");
            var (flat,flatRaw)=Fixture("ExpandedFlat",[150,120,160,110]);
            Check(new ElliottStructureValidator().Validate(flat,flatRaw).ValidationStatus=="Verified");
        });
        run("Elliott PDF X must be a correction, not one connecting line",()=>{
            var (s,raw)=Fixture("DoubleThree",[150,120,135,125]);
            Check(new ElliottStructureValidator().Validate(s,raw).ValidationStatus=="Verified");
            raw=raw.Where(p=>p.Time<=s.Waves[1].Time || p.Time>=s.Waves[2].Time).ToArray();
            Check(new ElliottStructureValidator().Validate(s,raw).ValidationStatus!="Verified");
        });
        run("Elliott PDF soft guidelines cannot change structural invalidation",()=>{
            var (s,raw)=Fixture("Impulse",[100,120,90,150,130,160]);
            var r=ElliottGuidelines.Apply(new ElliottStructureValidator().Validate(s,raw));
            Check(r.ValidationStatus=="Invalid");
            Check(r.Rules.Where(x=>x.Code is "depth-alternation" or "fifth-channel").All(x=>!x.IsHard));
        });
        run("Elliott PDF mirrored bearish structures have the same validity",()=>{
            var (s,raw)=Fixture("Impulse",[200,180,190,150,170,140]);
            Check(new ElliottStructureValidator().Validate(s,raw).ValidationStatus=="Verified");
        });
        run("Elliott PDF profiles and cache identities are separate",()=>{
            Check(new ElliottWaveAnalyzer(ElliottProfile.Classic).CacheVersion != new ElliottWaveAnalyzer(ElliottProfile.Copsey).CacheVersion);
            var (s,raw)=Fixture("HarmonicImpulse",[100,120,110,160,140,165]);
            var r=new ElliottStructureValidator(ElliottProfile.Copsey).Validate(s,raw);
            Check(r.ValidationStatus=="Verified" && r.Profile=="Copsey");
            Check(new ElliottStructureValidator().Validate(s with { Pattern="Impulse" },raw).ValidationStatus!="Verified");
        });
        run("Elliott PDF raw flat closes create no artificial waves",()=>{
            var values=new decimal[]{100,101,101,102,102,100,100,103};
            var raw=ElliottWaveAnalyzer.CloseTurns(values.Select((v,i)=>new BybitKline(i,v,v,v,v,1)).ToArray());
            Check(raw.Count==4 && raw[1].Price==102 && raw[2].Price==100);
        });
        run("Elliott PDF excludes unclosed minute and calendar-month candles",()=>{
            var start=new DateTimeOffset(2026,2,1,0,0,0,TimeSpan.Zero);
            var candle=new BybitKline(start.ToUnixTimeMilliseconds(),100,100,100,100,1);
            Check(ElliottWaveAnalyzer.ClosedCandles([candle],"1",start.AddSeconds(59)).Count==0);
            Check(ElliottWaveAnalyzer.ClosedCandles([candle],"1",start.AddMinutes(1)).Count==1);
            Check(ElliottWaveAnalyzer.ClosedCandles([candle],"M",start.AddDays(27)).Count==0);
            Check(ElliottWaveAnalyzer.ClosedCandles([candle],"M",start.AddMonths(1)).Count==1);
        });
        run("Elliott PDF 1000-candle analysis stays bounded and never upgrades unknowns",()=>{
            var candles=Enumerable.Range(0,1000).Select(i=>{var p=100m+(decimal)Math.Sin(i*.31)*7+i*.01m;return new BybitKline(i,p,p,p,p,1);}).ToArray();
            var clock=System.Diagnostics.Stopwatch.StartNew();
            var result=new ElliottWaveAnalyzer(ElliottProfile.Classic).Analyze(candles);
            Check(clock.Elapsed<TimeSpan.FromSeconds(15));
            Check(result.Scenarios.All(s=>s.Phase!="Complete" || s.ValidationStatus=="Verified" && s.SubdivisionCoveragePercent==100));
        });
        run("Elliott hierarchy finds the main five despite more than twenty internal turns",()=>{
            var (_,raw)=Fixture("Impulse",[100,120,110,150,130,160]);
            var candles=Candles(raw.Select(p=>p.Price).ToArray());
            var analysis=new ElliottWaveAnalyzer(ElliottProfile.Classic).Analyze(candles,2,.05m);
            var main=analysis.Scenarios.First();
            Check(main.Pattern=="Impulse" && main.ValidationStatus=="Verified");
            Check(main.Waves.Select(w=>w.Price).SequenceEqual(new decimal[]{100,120,110,150,130,160}));
            Check(main.Subwaves.Any(w=>w.Label=="5" && w.Degree==1));
            Check(main.Subwaves.Any(w=>w.Label=="C" && w.Degree==1));
            Check(main.Waves.All(w=>!w.IsTentative));
        });
        run("Elliott a new ABC retains the verified parent and its subdivisions",()=>{
            var (_,first)=Fixture("Impulse",[100,120,110,150,130,160]);
            var (_,second)=Fixture("Zigzag",[160,140,150,125]);
            var values=first[..^1].Select(p=>p.Price).Concat(second.Skip(1).Select(p=>p.Price)).ToArray();
            var main=new ElliottWaveAnalyzer(ElliottProfile.Classic).Analyze(Candles(values),2,.05m).Scenarios.First();
            var displayed=SignalChartRenderer.DisplayWaves(main);
            Check(displayed.Any(w=>w.Label=="1" && w.Degree==0 && w.Price==120));
            Check(displayed.Any(w=>w.Label=="5" && w.Degree==0 && w.Price==160));
            Check(displayed.Any(w=>w.Label=="C" && w.Degree==0 && w.Price==125));
            Check(displayed.Any(w=>w.Degree==1 && w.Label=="5"));
            Check(main.Coverage!.Sections.Count >= 2);
            foreach (var section in main.Coverage.Sections)
                Check(main.Subwaves.Where(w => w.Time > section.Start && w.Time <= section.End)
                    .All(w => w.Parent!.StartsWith(section.Id + "/", StringComparison.Ordinal)));
            Check(main.Coverage.Sections.All(section => section.Waves.All(w => w.Parent == section.Id)));
        });
        run("Elliott overlay separates structural degrees and excludes unverified foreign counts",()=>{
            var (s,raw)=Fixture("Impulse",[100,120,110,150,130,160]);
            var main=new ElliottStructureValidator().Validate(s,raw);
            main=main with { Waves=main.Waves.Select(w=>w with { Timeframe="60" }).ToArray(),
                ContextWaves=[new("W",4,125,-1){Timeframe="D",IsTentative=true}],
                Subwaves=main.Subwaves.Select(w=>w with {Timeframe="60"}).ToArray() };
            var displayed=SignalChartRenderer.DisplayWaves(main);
            Check(displayed.Any(w=>w.Degree==1));
            Check(displayed.All(w=>w.Timeframe!="D"));
            Check(SignalChartRenderer.FormatWaveLabel(main.Waves[1])=="(1)");
            Check(SignalChartRenderer.FormatWaveLabel(new("A",0,100,1))=="A");
        });
        run("Elliott degree grouping preserves outer extremes and endpoints",()=>{
            var points=new decimal[]{100,120,110,150,130,160}.Select((p,i)=>new ElliottPivot(i,i,p,i%2==0?"Low":"High")).ToArray();
            var degrees=ElliottPivotHierarchy.Degrees(points).ToArray();
            Check(degrees.All(d=>d[0]==points[0] && d[^1]==points[^1]));
            Check(degrees.All(d=>d.Zip(d.Skip(1)).All(x=>x.First.Kind!=x.Second.Kind)));
        });
        run("Elliott hierarchy keeps a triangle fourth wave at E rather than its deepest A",()=>{
            var (s,raw)=Fixture("Impulse",[100,120,110,150,137,160]);
            var (_,triangle)=Fixture("ContractingTriangle",[150,130,144,134,141,137]);
            var values=raw.Where(p=>p.Time<=s.Waves[3].Time).Select(p=>p.Price)
                .Concat(triangle[1..^1].Select(p=>p.Price))
                .Concat(raw.Where(p=>p.Time>s.Waves[4].Time).Select(p=>p.Price)).ToArray();
            var main=new ElliottWaveAnalyzer(ElliottProfile.Classic).Analyze(Candles(values),2,.05m).Scenarios.First();
            Check(main.Pattern=="Impulse" && main.ValidationStatus=="Verified");
            Check(main.Waves[4].Price==137 && main.Structure!.Children[3].Pattern=="ContractingTriangle");
            Check(main.Subwaves.Any(w=>w.Label=="E"));
        });
    }
    internal static BybitKline[] Candles(decimal[] turns)
    {
        var values=new List<decimal>{turns[0]};
        for(var leg=0;leg<turns.Length-1;leg++)
            for(var step=1;step<=8;step++) values.Add(turns[leg]+(turns[leg+1]-turns[leg])*step/8m);
        return values.Select((v,i)=>new BybitKline(i*60_000L,v,v+.1m,v-.1m,v,1)).ToArray();
    }
    private static void Check(bool condition) { if(!condition) throw new Exception("Elliott PDF rule assertion failed"); }
    private static bool Geometry(string pattern,decimal[] prices)=>ElliottStructureValidator.Geometry(pattern,
        prices.Select((v,i)=>new ElliottPivot(i,i,v,(i%2==0)==(prices[1]>prices[0])?"Low":"High")).ToArray());
    internal static (ElliottScenario,ElliottPivot[]) Fixture(string pattern,decimal[] prices,bool firstAsCorrection=false)
    {
        var points=new List<ElliottPivot>(); var waves=new List<ElliottWavePoint>();
        string[] labels=pattern is "Zigzag" or "Flat" or "ExpandedFlat" ? ["0","A","B","C"]
            : pattern=="ContractingTriangle" ? ["0","A","B","C","D","E"]
            : pattern=="DoubleThree" ? ["0","W","X","Y"] : ["0","1","2","3","4","5"];
        for(var i=0;i<prices.Length-1;i++)
        {
            bool motive=pattern is "Impulse" or "TruncatedImpulse" ? i%2==0
                : pattern=="Zigzag" ? i!=1 : pattern is "Flat" or "ExpandedFlat" ? i==2 : false;
            if(firstAsCorrection && i==0) motive=false;
            decimal[] shape=motive ? [0,.3m,.15m,.7m,.55m,1] : [0,.7m,.35m,1];
            if(i==0) points.Add(new(0,0,prices[0],prices[1]>prices[0]?"Low":"High"));
            waves.Add(new(labels[i],points[^1].Time,prices[i]));
            for(var j=1;j<shape.Length;j++)
            {
                var value=prices[i]+(prices[i+1]-prices[i])*shape[j];
                points.Add(new(points.Count,points.Count,value,value>points[^1].Price?"High":"Low"));
            }
        }
        waves.Add(new(labels[^1],points[^1].Time,prices[^1]));
        points.Add(new(points.Count,points.Count,prices[^1]+(prices[^1]>prices[^2]?-1:1),prices[^1]>prices[^2]?"Low":"High"));
        return (new(prices[1]>prices[0]?"Bullish":"Bearish",70,waves,[],prices[0],prices[^2],new(0,0,0),pattern,"Complete","","test"),points.ToArray());
    }
}

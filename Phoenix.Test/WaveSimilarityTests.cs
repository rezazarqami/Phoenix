using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;
using System.Text.Json;

internal static class WaveSimilarityTests
{
    private static void Check(bool v,string message) { if(!v)throw new Exception(message); }
    private static TechnicalFeatureSnapshot Features => new(.5m,1,0,0,0,1,1,.5m,.5m,1,.5m)
    { Wave=new(1,10,20,100,200,.9m,.9m,.8m,.1m,.05m,.1m,.8m,.05m,new decimal[8]) };
    public static void Counts()
    {
        var now=DateTime.UtcNow; var features=Features;
        var query=new ServerSignal{Id=Guid.NewGuid(),Symbol="QUERY",Timeframe="15",CreatedAtUtc=now,TechnicalFeatures=features};
        var patterns=new List<LearnedSignalPattern>();
        foreach(var interval in new[]{"15","D"})
        foreach(var outcome in new[]{"Target","StopLoss","RiskFree"})
        for(var i=0;i<31;i++) patterns.Add(new(new ServerSignal{Id=Guid.NewGuid(),Symbol=$"{outcome}{i}",Timeframe=interval,
            CompletedAtUtc=now.AddDays(-1)},outcome,features));
        var e=SignalSimilarityService.CalculateFromPatterns(query,patterns).Evidence!;
        Check(e.SameTimeframeNearbyTargets==31 && e.SameTimeframeNearbyStops==31 && e.SameTimeframeNearbyRiskFree==31 &&
            e.OtherTimeframeNearbyTargets==31 && e.OtherTimeframeNearbyStops==31 && e.OtherTimeframeNearbyRiskFree==31,"All six counts; no sixty-row or nearest-N cap");
        var summary=SignalReviewText.Support(e);
        Check(summary=="\nنزدیکِ کل: 62 تارگت / 62 استاپ / 62 ریسک‌فری","Same and other timeframes render one combined row");
        var caption="levels\nنزدیکِ هم‌تایم‌فریم: 31 تارگت / 31 استاپ / 31 ریسک‌فری\nنزدیکِ سایر تایم‌فریم‌ها: 31 تارگت / 31 استاپ / 31 ریسک‌فری";
        var combined=SignalReviewText.CurrentSupport(caption,e);
        Check(combined=="levels"+summary && SignalReviewText.CurrentSupport(combined,e)==combined,
            "Existing frozen proposals combine counts without duplicated rows or reanalysis");
        patterns.Add(patterns[0] with{Signal=new ServerSignal{Id=Guid.NewGuid(),Symbol=patterns[0].Signal.Symbol,Timeframe="15",CompletedAtUtc=now.AddDays(-1)}});
        Check(SignalSimilarityService.CalculateFromPatterns(query,patterns).Evidence!.SameTimeframeNearbyTargets==31,"Repeated anchor pair must count once");
        patterns.Add(new(new ServerSignal{Id=Guid.NewGuid(),Symbol="FUTURE",Timeframe="15",CompletedAtUtc=now.AddDays(1)},"Target",features));
        Check(SignalSimilarityService.CalculateFromPatterns(query,patterns).Evidence!.SameTimeframeNearbyTargets==31,"Future outcomes excluded");
        query.TechnicalFeatures=features with{Wave=features.Wave! with{EntryCrossingPower=0}};
        var zero=SignalSimilarityService.CalculateFromPatterns(query,patterns);
        Check(Total(zero.Evidence!)==0 && zero.TargetPercent is null && zero.Evidence!.NearestExamples!.Count==0,"Dissimilar entry structure gives six true zeros without percentages or charts");
        query.TechnicalFeatures=features with{Wave=null};
        Check(Total(SignalSimilarityService.CalculateFromPatterns(query,patterns).Evidence!)==0,"Missing legacy wave never becomes matching zero");
        query.TechnicalFeatures=features with{Rsi14=0,VolumeRatio=999,AtrPercent=0,TrendStrength=-1};
        Check(Total(SignalSimilarityService.CalculateFromPatterns(query,patterns).Evidence!)==186,"Unrelated indicators never influence wave counts");
    }
    private static int Total(SimilarityEvidence e)=>e.SameTimeframeNearbyTargets+e.SameTimeframeNearbyStops+e.SameTimeframeNearbyRiskFree+
        e.OtherTimeframeNearbyTargets+e.OtherTimeframeNearbyStops+e.OtherTimeframeNearbyRiskFree;
    public static void ArchiveRecovery() => Recover().GetAwaiter().GetResult();
    private static async Task Recover()
    {
        var root=Path.Combine(Path.GetTempPath(),"phoenix-wave-"+Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var archive=new ReviewArchiveStore(Path.Combine(root,"review.db"));
            var bars=Enumerable.Range(0,60).Select(i=>i<9
                ? new BybitKline(100+i,100+i*10,111+i*10,100+i*10,110+i*10,1)
                : new BybitKline(100+i,180,190,170,180,1)).ToArray();
            var c=new SignalCandidate("TEST","15","Long",191,100,180,140,150,130,null,null,1,1,0,108,100,100,159,60,"",false,null);
            var id=Guid.NewGuid();
            await archive.SaveAsync("wave",c,bars,"15",false,[1],default);
            await archive.LinkSignalAsync("wave",id,default);
            var recovered=await archive.GetTechnicalFeaturesAsync([id],default);
            Check(recovered[id].Wave is not null,"Legacy review archive recovers exact wave without market requests");
            Check(JsonSerializer.Serialize(recovered[id].Wave)==JsonSerializer.Serialize(WaveFingerprint.Extract(bars,c)),"Archive recovery uses original anchors");
        }
        finally { SignalHistoryStore.ClearConnectionPools(); Directory.Delete(root,true); }
    }
    public static void Extraction()
    {
        BybitKline[] Bars(long step,decimal scale)=>Enumerable.Range(0,9).Select(i=>new BybitKline(100+i*step,
            (100+i*10)*scale,(111+i*10)*scale,(100+i*10)*scale,(110+i*10)*scale,1)).ToArray();
        SignalCandidate Candidate(long step,decimal scale)=>new("TEST","15","Long",191*scale,100*scale,190*scale,140*scale,150*scale,130*scale,
            null,null,1,1,0,100+8*step,100,100,100+8*step,9,"",false,null);
        var a=WaveFingerprint.Extract(Bars(1,1),Candidate(1,1))!;
        var b=WaveFingerprint.Extract(Bars(60000,10),Candidate(60000,10))!;
        Check(a is not null && b is not null,"Exact anchors extract");
        var f=Features with{Wave=a}; var g=Features with{Wave=b};
        Check(TechnicalResemblance.Compare(new(),new(),f,g).Nearby,"Price scaling and elapsed time do not affect similarity");
        var future=Bars(1,1).Append(new BybitKline(500,1000,2000,1,10,1)).ToArray();
        Check(JsonSerializer.Serialize(a)==JsonSerializer.Serialize(WaveFingerprint.Extract(future,Candidate(1,1))),"Candles after wave cannot change fingerprint");
        Check(WaveFingerprint.Extract(Bars(1,1),Candidate(1,1) with{CeilingTime=999}) is null,"Missing exact anchor is unknown");
        var frozen=JsonSerializer.Deserialize<TechnicalFeatureSnapshot>(JsonSerializer.Serialize(f))!;
        Check(TechnicalResemblance.Compare(new(),new(),f,frozen).Score==1,"Fingerprint survives JSON archive roundtrip");
        var burned=SignalQualityAssessment.PostFormationValidity("Long",140,150,108,
            [new(109,160,165,139,145,1),new(110,145,151,144,150,1)]);
        Check(burned.Invalid && burned.Reason=="EntryTouchedByWick","First entry touch consumes the wave; return to target cannot revive it");
    }
}

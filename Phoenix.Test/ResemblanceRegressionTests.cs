using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;

internal static class ResemblanceRegressionTests
{
    private static readonly TechnicalFeatureSnapshot Baseline=new(.5m,1m,0m,0m,0m,1m,1m,.5m,.5m,1m,.5m,
        EntryLevelStrength:.25m,StopLevelStrength:.5m,TargetLevelStrength:.75m,SimilarityContextVersion:3)
        {TradeGeometry=new(1,1,2,2,1)};
    public static void RepeatedCountsAndGenuineZeros()
    {
        var at=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
        var rows=new List<LearnedSignalPattern>();
        foreach(var (timeframe,targets,stops,risk) in new[]{("1",4,3,4),("15",8,6,1)})
        foreach(var (outcome,count) in new[]{("Target",targets),("StopLoss",stops),("RiskFree",risk)})
        for(var i=0;i<count;i++)rows.Add(new(new ServerSignal{Id=Guid.NewGuid(),Symbol="HISTORYUSDT",Direction="Long",
            Timeframe=timeframe,CompletedAtUtc=at.AddDays(-1)},outcome,Baseline));
        SignalSimilarityResult Query(TechnicalFeatureSnapshot features)=>SignalSimilarityService.CalculateFromPatterns(
            new ServerSignal{Direction="Long",Timeframe="1",CreatedAtUtc=at,TechnicalFeatures=features},rows);
        var same=Query(Baseline).Evidence!;
        Check(same.SameTimeframeNearbyTargets==4 && same.SameTimeframeNearbyStops==3 && same.SameTimeframeNearbyRiskFree==4 &&
            same.OtherTimeframeNearbyTargets==8 && same.OtherTimeframeNearbyStops==6 && same.OtherTimeframeNearbyRiskFree==1,"Identical observations must count exactly, without quotas");
        foreach(var changed in new[]{Baseline with{SupportDistanceAtr=8,ResistanceDistanceAtr=9},
            Baseline with{TradeGeometry=new(1,8,16,2,1)},Baseline with{IchimokuPosition=3,IchimokuCloudBias=3}})
        {
            var result=Query(changed).Evidence!;
            Check(Total(result)==0,"Contradictory SR, planned stop/target geometry or cloud evidence must genuinely produce six zeros");
            Check(result.ComparableTargets==12 && result.ComparableStops==9 && result.ComparableRiskFree==5,
                "A measured zero must retain its actual comparison denominator");
            Check(result.NearestExamples!.All(e=>!e.Nearby) && result.Metric==TechnicalResemblance.Metric,
                "Saved example IDs and group distances must explain rejection");
        }
        Check(Total(Query(Baseline with{VolumeRatio=999,Rsi14=0,PriceActionScore=0,BitcoinMarketAlignment=-1}).Evidence!)==26,
            "Excluded indicators must not move any count");
        var unavailable=Query(Baseline with{AtrPercent=0}).Evidence!;
        Check(Total(unavailable)==0 && unavailable.InsufficientFeatureCount==26 && unavailable.ComparableTargets==0,
            "Missing measurements must be separate from a genuinely dissimilar sample");
    }
    public static void DifferentQueriesChangeNeighbors()
    {
        var rows=Enumerable.Range(0,30).Select(i=>new LearnedSignalPattern(new ServerSignal{Id=Guid.NewGuid(),Direction="Long",Timeframe="15"},
            i%3==0?"Target":i%3==1?"StopLoss":"RiskFree",Baseline with{SupportDistanceAtr=.2m+i*.2m,ResistanceDistanceAtr=.2m+i*.2m})).ToArray();
        var counts=new List<int>();
        foreach(var distance in new[]{.2m,1m,2m,4m,10m,40m})
        {
            var signal=new ServerSignal{Direction="Long",Timeframe="15",TechnicalFeatures=Baseline with{SupportDistanceAtr=distance,ResistanceDistanceAtr=distance}};
            counts.Add(Total(SignalSimilarityService.CalculateFromPatterns(signal,rows).Evidence!));
        }
        Check(counts.Distinct().Count()>=4 && counts.Contains(0) && counts.Any(c=>c>0),
            "Changing technical context must change membership, including a real zero: "+string.Join(",",counts));
    }
    public static void UncappedStoredTradeLevels()
    {
        var candles=Enumerable.Range(0,100).Select(i=>new BybitKline(i*60_000L,100,102,98,100,1)).ToArray();
        SignalCandidate Candidate(decimal entry)=>new("TESTUSDT","15","Long",2200,80,100,entry,entry+20,entry-10,null,null,1,1,0,0,0,0,0,100,"fixture",false,null);
        var first=TechnicalFeatureExtractor.Calculate(candles,Candidate(1000));
        var second=TechnicalFeatureExtractor.Calculate(candles,Candidate(2000));
        Check(first.IchimokuEntryPosition==1 && second.IchimokuEntryPosition==1 && first.SupportDistanceAtr==10 && second.SupportDistanceAtr==10,
            "Fixture reproduces the old clipped identical readings");
        Check(first.LevelContext!.Entry.CloudDistanceAtr!=second.LevelContext!.Entry.CloudDistanceAtr &&
            first.LevelContext.Entry.SupportDistanceAtr!=second.LevelContext.Entry.SupportDistanceAtr,
            "Persist uncapped cloud and actual pivot distances, rather than identical saturated values");
        var signal=new ServerSignal{Direction="Long",Timeframe="15"};
        var match=TechnicalResemblance.Compare(signal,signal,first,second);
        Check(match.Comparable && !match.Nearby && match.Distances["ichimoku"]>1,
            "Different uncapped trade levels must no longer become near merely because both old values saturated");
        var historical=first with{LevelContext=null};
        Check(TechnicalResemblance.Compare(signal,signal,historical,historical).Comparable==false,
            "Two capped legacy distances cannot invent precise support/resistance evidence");
    }
    private static int Total(SimilarityEvidence e)=>e.SameTimeframeNearbyTargets+e.SameTimeframeNearbyStops+e.SameTimeframeNearbyRiskFree+
        e.OtherTimeframeNearbyTargets+e.OtherTimeframeNearbyStops+e.OtherTimeframeNearbyRiskFree;
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}

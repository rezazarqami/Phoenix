using Phoenix.Web;

internal static class SupportResistanceOnlyTests
{
    public static void Run()
    {
        var f = new TechnicalFeatureSnapshot(.5m,1,0,0,0,1,1,.5m,.5m,1,.5m,
            EntryLevelStrength:.5m,StopLevelStrength:.5m,TargetLevelStrength:.5m)
        {LevelContext = new(new(.5m,1m,0,4){SupportTouches=5,ResistanceTouches=8},
            new(.1m,2m,0,2),new(3m,.2m,0,3)),TradeGeometry=new(1,1,2,2,1)};
        var signal = new ServerSignal{Direction="Long",Timeframe="15",TechnicalFeatures=f};
        var unrelated = f with {Rsi14=0,TrendStrength=-1,IchimokuPosition=99,IchimokuCloudBias=-99,
            FibonacciAlignment=0,PriceActionScore=0,VolumeRatio=999,ImpulseEfficiency=0,
            HigherTimeframeAlignment=-1,BitcoinMarketAlignment=-1,MarketRegimeScore=1,
            StructureScore=-1,LiquiditySweepScore=1,IchimokuEntryPosition=-50,
            IchimokuStopPosition=50,IchimokuTargetPosition=40,StructureBreakScore=-1,StructureRetestScore=1,
            TradeGeometry=new(100,500,900,2,40),AtrPercent=10,
            LevelContext=new(f.LevelContext.Entry with{CloudDistanceAtr=99},
                f.LevelContext.Stop with{CloudDistanceAtr=-99},f.LevelContext.Target with{CloudDistanceAtr=80})};
        var match=TechnicalResemblance.Compare(signal,signal,f,unrelated);
        Check(match.Score==1 && match.Nearby,"Changing every non-SR field must leave the result exactly identical");
        var scale=new MultiScaleLevelSnapshot("60",240,0,0,0,.5m,.5m,.5m,0,0,0,0){LevelContext=f.LevelContext};
        var multiscale=f with{MultiScaleLevels=[scale]};
        var changedScale=multiscale with{MultiScaleLevels=[scale with{FibonacciEntry=1,FibonacciStop=1,FibonacciTarget=1,
            CloudEntry=99,CloudStop=-99,CloudTarget=80,CloudBias=-80}]};
        Check(TechnicalResemblance.Compare(signal,signal,multiscale,changedScale).Score==1,
            "Non-SR fields in every timeframe/window must also be excluded");
        var weak=f with{LevelContext=f.LevelContext with{Entry=f.LevelContext.Entry with{SupportTouches=0,ResistanceTouches=0,NearbyPivots=0}}};
        Check(TechnicalResemblance.Compare(signal,signal,f,weak).Score<1,"Power of adjacent entry levels must affect similarity");
        foreach(var c in new[]{f with{LevelContext=f.LevelContext with{Stop=new(50,50,0,2)}},
            f with{LevelContext=f.LevelContext with{Target=new(50,50,0,3)}}})
            Check(!TechnicalResemblance.Compare(signal,signal,f,c).Nearby,"Contradictory SR at stop or target must be excluded");
        var onlyEntry=f with{EntryLevelStrength=0}; var onlyTarget=f with{TargetLevelStrength=0};
        Check(TechnicalResemblance.Compare(signal,signal,f,onlyEntry).Score <
            TechnicalResemblance.Compare(signal,signal,f,onlyTarget).Score,"Entry strength must carry more weight than target strength");
        var at=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
        var rows=Enumerable.Range(0,180).Select(i=>new SignalHistoryItem(new ServerSignal{
            Id=Guid.NewGuid(),Symbol="TEST"+i,Direction="Long",Timeframe=i%2==0?"15":"60",
            EntryPrice=100,TakeProfit=110,StopLoss=90, // No ceiling/floor: valid frozen SR still counts.
            EntryTriggeredAtUtc=at.AddDays(-2),CompletedAtUtc=at.AddDays(-1),Outcome=i%3==0?"Target":i%3==1?"StopLoss":"RiskFree",
            ProposalEvidence=new(at.AddDays(-3),"TEST"+i,"Long",i%2==0?"15":"60",100,110,90,f,new(null,null,0,0,0))
        },at.AddDays(-1),null,false)).ToArray();
        var (_,patterns)=SignalLearningService.BuildPatterns(rows);
        Check(patterns.Length==180,"Frozen valid signals without floor/ceiling must survive full-archive preparation");
        var result=SignalSimilarityService.CalculateFromPatterns(new ServerSignal{
            Direction="Long",Timeframe="15",CreatedAtUtc=at,TechnicalFeatures=f},patterns);
        Check(result.Evidence is {EvaluatedSnapshotCount:180,ComparableTargets:60,ComparableStops:60,ComparableRiskFree:60,
            SameTimeframeNearbyTargets:30,OtherTimeframeNearbyTargets:30},"Every historical outcome and timeframe must contribute exactly once");
    }
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}

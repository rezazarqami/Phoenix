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
        foreach(var changed in new[]{Baseline with{SupportDistanceAtr=8,ResistanceDistanceAtr=9}})
        {
            var result=Query(changed).Evidence!;
            Check(Total(result)==0,"Contradictory SR distances must genuinely produce six zeros");
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
        var second=TechnicalFeatureExtractor.Calculate(candles,Candidate(8000));
        Check(first.IchimokuEntryPosition==1 && second.IchimokuEntryPosition==1 && first.SupportDistanceAtr==10 && second.SupportDistanceAtr==10,
            "Fixture reproduces the old clipped identical readings");
        Check(first.LevelContext!.Entry.CloudDistanceAtr!=second.LevelContext!.Entry.CloudDistanceAtr &&
            first.LevelContext.Entry.SupportDistanceAtr!=second.LevelContext.Entry.SupportDistanceAtr,
            "Persist uncapped cloud and actual pivot distances, rather than identical saturated values");
        var signal=new ServerSignal{Direction="Long",Timeframe="15"};
        var match=TechnicalResemblance.Compare(signal,signal,first,second);
        Check(match.Comparable && !match.Nearby && match.Distances["support-resistance"]>1,
            "Different uncapped trade levels must no longer become near merely because both old values saturated");
        var historical=first with{LevelContext=null};
        Check(TechnicalResemblance.Compare(signal,signal,historical,historical).Comparable==false,
            "Two capped legacy distances cannot invent precise support/resistance evidence");
    }
    public static void LegacyArchiveBeyondSixty()
    {
        var at=new DateTime(2026,10,1,0,0,0,DateTimeKind.Utc);
        var history=Enumerable.Range(0,183).Select(i=>
        {
            var observed=at.AddDays(-10).AddMinutes(i);
            var f=Baseline with { SimilarityContextVersion=i%4,
                SupportDistanceAtr=i%3==0?1m:8m,ResistanceDistanceAtr=i%3==0?1m:9m };
            var signal=new ServerSignal { Id=Guid.NewGuid(),Symbol="LEGACYUSDT",Direction="Long",
                Timeframe=i<90?"60":"15",Floor=80,Ceiling=120,EntryPrice=100,StopLoss=90,TakeProfit=110,
                CreatedAtUtc=observed,EntryTriggeredAtUtc=observed.AddMinutes(1),CompletedAtUtc=observed.AddMinutes(2),
                Outcome=i%3==0?"Target":i%3==1?"StopLoss":"RiskFree",
                ProposalEvidence=new(observed,"LEGACYUSDT","Long",i<90?"60":"15",100,110,90,f,new(null,null,0,0,0)) };
            return new SignalHistoryItem(signal,observed.AddMinutes(2),null,false);
        }).ToArray();
        var (_,proposals)=SignalLearningService.BuildPatterns(history);
        Check(proposals.Length==183,"All frozen proposal versions must survive preparation, beyond 60 rows");
        var candidate=new ServerSignal { Direction="Long",Timeframe="60",CreatedAtUtc=at,TechnicalFeatures=Baseline };
        var result=SignalSimilarityService.CalculateFromPatterns(candidate,proposals).Evidence!;
        Check(result.SameTimeframeComparable==90 && result.OtherTimeframeComparable==93,
            "Denominators must include the full mixed-version archive in the correct timeframe");
        Check(result.SameTimeframeNearbyTargets==30 && result.OtherTimeframeNearbyTargets==31 && Total(result)==61,
            "Only measured nearby targets should count; distant stop/risk-free samples must not be inflated");
        var changedCandidate=new ServerSignal {Direction="Long",Timeframe="60",CreatedAtUtc=at,
            TechnicalFeatures=Baseline with {SupportDistanceAtr=8,ResistanceDistanceAtr=9}};
        var changed=SignalSimilarityService.CalculateFromPatterns(changedCandidate,proposals);
        Check(Total(changed.Evidence!)==122,"A different query must select the other 122 observations");
    }

    public static void RawLevelsWithoutLegacyReadings()
    {
        var level=new TechnicalLevelGeometry(20,30,8,0);
        var features=Baseline with {SupportDistanceAtr=10,ResistanceDistanceAtr=10,
            EntryLevelStrength=null,StopLevelStrength=null,TargetLevelStrength=null,
            LevelContext=new(level,level,level)};
        var signal=new ServerSignal {Direction="Long",Timeframe="60"};
        Check(TechnicalResemblance.Compare(signal,signal,features,features).Nearby,
            "Complete raw measurements must remain comparable even when every legacy distance saturated");
        var clipped=Baseline with {IchimokuPosition=1,IchimokuCloudBias=1,
            IchimokuStopPosition=1,IchimokuTargetPosition=1};
        var match=TechnicalResemblance.Compare(signal,signal,clipped,clipped);
        Check(!match.Distances.ContainsKey("ichimoku"),"Equal clipped bounds cannot invent precise cloud evidence");
    }

    public static void QueuedProposalMigration() => Migrate().GetAwaiter().GetResult();
    private static async Task Migrate()
    {
        var root=Path.Combine(Path.GetTempPath(),"phoenix-similarity-migration-"+Guid.NewGuid());
        Directory.CreateDirectory(root);
        var oldPath=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",Path.Combine(root,"queue.json"));
            var observed=DateTime.UtcNow.AddDays(-2);
            var store=new ServerOrderStore(Path.Combine(root,"queue.json"),Path.Combine(root,"history.db"));
            var shadow=new ShadowSignalRuntime();
            var candidate=new SignalCandidate("TESTUSDT","60","Long",120,80,101,100,110,90,
                92,105,10,1,0,0,0,0,0,100,"fixture",false,0);
            foreach(var (outcome,completed) in new[]{("Target",observed.AddHours(-1)),("StopLoss",observed.AddHours(1))})
                await store.AddAsync(new ServerSignal {Id=Guid.NewGuid(),Symbol=outcome+"USDT",Direction="Long",
                    Timeframe="60",Ceiling=120,Floor=80,EntryPrice=100,TakeProfit=110,StopLoss=90,Outcome=outcome,
                    CreatedAtUtc=observed.AddDays(-2),EntryTriggeredAtUtc=observed.AddDays(-1),CompletedAtUtc=completed,
                    ProposalEvidence=new(observed.AddDays(-2),outcome+"USDT","Long","60",100,110,90,Baseline,new(null,null,0,0,0))});
            using var learning=new SignalLearningService(store,shadow,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SignalLearningService>.Instance);
            var service=new ProfessionalSignalAnalysisService(null!,new SignalSimilarityService(learning));
            var prediction=new SignalSimilarityResult(null,null,60,0,0) {Evidence=new("proposal","proposal",0,0,0,0,0,0,0,0)
                {ComparisonVersion=4,OtherTimeframeComparable=60}};
            var analysis=new ProfessionalSignalAnalysis(Baseline,prediction,"Range",[],[]) {ObservedAtUtc=observed};
            var batches=new SignalBatchService(null!,null!,null!,store,null!,null!,null!,service,shadow,null!,null!,null!);
            await batches.AddProposalAsync(new("owner-key","owner",candidate,"60",true,analysis,
                "chart and levels"+SignalReviewText.Support(prediction.Evidence),[1,2],5,observed),default);
            await batches.AddProposalAsync(new("other-key","other",candidate,"60",true,analysis,"other caption",[3],5,observed),default);
            var item=(await batches.PendingAsync("owner",default)).Single();
            Check(item.Analysis.Prediction.Evidence is {ComparisonVersion:6,SameTimeframeNearbyTargets:1,SameTimeframeNearbyStops:0},
                "Migrate old desk counts with only outcomes known at the original proposal time");
            Check(item.Analysis.Features==Baseline && item.Analysis.ObservedAtUtc==observed && item.Image.SequenceEqual(new byte[]{1,2}) &&
                item.Caption.StartsWith("chart and levels") && !item.Caption.Contains("60"),
                "Migration preserves the frozen measurements, timestamp, chart and caption levels");
            var repeated=(await batches.PendingAsync("owner",default)).Single();
            Check(repeated.Caption==item.Caption,"Repeated reads must not append duplicate summaries");
            using var saved=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"in-app-proposals.json")));
            Check(saved.RootElement[1].GetProperty("analysis").GetProperty("prediction").GetProperty("evidence")
                .GetProperty("comparisonVersion").GetInt32()==4,"Another owner's proposal must remain untouched");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",oldPath);
            SignalHistoryStore.ClearConnectionPools();Directory.Delete(root,true);
        }
    }

    private static int Total(SimilarityEvidence e)=>e.SameTimeframeNearbyTargets+e.SameTimeframeNearbyStops+e.SameTimeframeNearbyRiskFree+
        e.OtherTimeframeNearbyTargets+e.OtherTimeframeNearbyStops+e.OtherTimeframeNearbyRiskFree;
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}

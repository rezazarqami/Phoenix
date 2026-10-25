using Phoenix.Web;

internal static class OutcomeWindowTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static void FirstEventAndMissingness()
    {
        var at=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);var deadline=at.AddHours(2);var now=at.AddDays(2);
        var s=new ServerSignal {EntryTriggeredAtUtc=at,TargetReachedAtUtc=at.AddMinutes(20),StopLossReachedAtUtc=at.AddMinutes(40),CompletedAtUtc=at.AddHours(3),Outcome="StopLoss"};
        string Label()=>OutcomeWindows.Classify(new(s,now,null),at,deadline,now);
        Check(Label()=="Target","First registered event, not eventual overwritten outcome, determines the window");
        s.StopLossReachedAtUtc=s.TargetReachedAtUtc;Check(Label()=="Unknown","Simultaneous events have no inferred order");
        s.StopLossReachedAtUtc=null;s.RiskFreeClosedAtUtc=at.AddMinutes(10);Check(Label()=="RiskFree","Risk-free closure competes with target rather than becoming stop");
        s.RiskFreeClosedAtUtc=null;s.TargetReachedAtUtc=at.AddHours(3);Check(Label()=="NoRecordedResolution","A later target cannot count inside the horizon");
        Check(OutcomeWindows.Classify(new(s,at.AddMinutes(15),null),at,deadline,now)=="Unknown","Stale monitoring is not proof of an unresolved market path");
        Check(OutcomeWindows.Classify(new(s,now,null),at,deadline,at.AddHours(1))=="Immature","Do not count a not-yet-mature window as a loss");
        s.TargetReachedAtUtc=null;s.EntryTriggeredAtUtc=null;s.ExpiredAtUtc=at.AddMinutes(40);s.CompletedAtUtc=s.ExpiredAtUtc;s.Status="Expired";
        Check(Label()=="Expired","Unentered expired proposals must not become stops");
        s.RecoveryOriginalStatus="Pending";Check(Label()=="Unknown","Quarantined histories cannot supply resolved evidence");
    }
    public static void DenominatorsAndCohorts()
    {
        var at=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
        var f=new TechnicalFeatureSnapshot(.5m,1,0,0,0,1,1,.5m,.5m,1,.5m);
        var candidate=new ServerSignal {Id=Guid.NewGuid(),CreatedAtUtc=at.AddDays(20),Direction="Long",Timeframe="5",EntryPrice=100,TakeProfit=110,StopLoss=90,TechnicalFeatures=f};
        var history=Enumerable.Range(0,10).Select(i=>
        {
            var entry=at.AddDays(i);var s=new ServerSignal {Id=Guid.NewGuid(),Symbol="TEST",CreatedAtUtc=entry,EntryTriggeredAtUtc=entry,Direction="Long",Timeframe="5",EntryPrice=100,TakeProfit=110,StopLoss=90,EntryTechnicalFeatures=f};
            if(i<3){s.TargetReachedAtUtc=entry.AddMinutes(5);s.CompletedAtUtc=s.TargetReachedAtUtc;s.Outcome="Target";}
            else if(i<5){s.StopLossReachedAtUtc=entry.AddMinutes(10);s.CompletedAtUtc=s.StopLossReachedAtUtc;s.Outcome="StopLoss";}
            else if(i==5){s.RiskFreeClosedAtUtc=entry.AddMinutes(30);s.CompletedAtUtc=s.RiskFreeClosedAtUtc;s.Outcome="RiskFree";}
            return (Source:"main",Item:new SignalHistoryItem(s,entry.AddHours(3),null));
        }).ToArray();
        var windows=OutcomeWindows.Calculate(candidate,history,"entry");var w=windows[0];
        Check(w.MatureCount==10 && w.RecordedTargetShare==30 && w.RecordedStopShare==20 && w.RiskFree==1 && w.NoRecordedResolution==4,"All mature categories belong in the denominator, not just target and stop");
        Check(windows[1].MatureCount==0,"Observation and real histories must remain separate");
        Check(OutcomeWindows.Calculate(candidate,history.Concat(history).ToArray(),"entry")[0].MatureCount==10,"Duplicated opportunities cannot inflate window counts");
        Check(OutcomeWindows.Calculate(candidate,history,"proposal")[0].MatureCount==0,"Missing frozen proposal evidence cannot become entry evidence silently");
        Check(OutcomeWindows.Horizon("5")==120 && OutcomeWindows.Horizon("unknown") is null,"Window is 24 declared bars; unsupported intervals must abstain");
        Check(SignalReviewText.Window(w).Contains("آمار گذشته"),"Window shares must not be described as signal probabilities");
        var observations=history.Select(x=>(Source:"observation",x.Item)).ToArray();
        var legacy=OutcomeWindows.Calculate(candidate,observations,"entry")[1];
        Check(legacy.LegacyObservationCount==10 && legacy.RecordedTargetShare is null,
            "Legacy observations must not advertise mixed-policy outcome percentages");
        foreach(var row in observations)row.Item.Signal.ObservationPolicyVersion=1;
        var current=OutcomeWindows.Calculate(candidate,observations,"entry")[1];
        Check(current.LegacyObservationCount==0 && current.RecordedTargetShare==30,
            "Versioned observations retain all outcomes in their denominator");
        candidate.TakeProfit=120;
        Check(TradePayoffEvidence.From(candidate) is { RewardRisk:2,GrossBinaryBreakEvenRate:33.3m },"Win counts must be interpreted alongside risk/reward, without claiming net profitability");
        candidate.StopLoss=101;
        Check(TradePayoffEvidence.From(candidate) is null,"Invalid trade geometry must not produce a breakeven percentage");
    }
}

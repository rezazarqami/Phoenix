using Phoenix.Web;

internal static class ObservationOutcomeTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Transitions()
    {
        PersistedPaperResult().GetAwaiter().GetResult();
        var at = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        foreach (var sign in new[] { 1m, -1m })
        {
            ServerSignal New() => new() { Id = Guid.NewGuid(), Direction = sign == 1 ? "Long" : "Short",
                Status = "Filled", EntryPrice = 100, TakeProfit = 100 + sign * 20,
                StopLoss = 100 - sign * 10, RiskFreePrice = 100 + sign * 15,
                StopLoss2 = 100 + sign * 10, ObservationPolicyVersion = 1, EntryTriggeredAtUtc = at };
            var s = New();
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 11, at.AddSeconds(1));
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 9, at.AddSeconds(2));
            Check(s.Outcome is null && s.RiskFreeReachedAtUtc is null, "Protection must first activate");
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 15, at.AddSeconds(3));
            Check(s.RiskFreeReachedAtUtc == at.AddSeconds(3) && s.Outcome is null, "Activation is not closure");
            s = System.Text.Json.JsonSerializer.Deserialize<ServerSignal>(System.Text.Json.JsonSerializer.Serialize(s))!;
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 10, at.AddSeconds(4));
            Check(s.Outcome == "RiskFree" && s.RiskFreeClosedAtUtc == at.AddSeconds(4) && s.StopLossReachedAtUtc is null,
                "Restart preserves protection and risk-free must not become a stop");
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 20, at.AddSeconds(5));
            Check(s.Outcome == "RiskFree", "Terminal outcomes cannot be overwritten");
            s = New();
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 15, at.AddSeconds(1));
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 20, at.AddSeconds(2));
            Check(s.Outcome == "Target", "Target wins when price continues to target");
            s = New();
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 15, at.AddSeconds(1));
            ObservationOutcomeTracker.TrackFilled(s, 100 - sign * 12, at.AddSeconds(2));
            Check(s.Outcome == "StopLoss", "A gap cannot imply a successful protected exchange fill");
            s = New(); s.ObservationPolicyVersion = null;
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 15, at.AddSeconds(1));
            ObservationOutcomeTracker.TrackFilled(s, 100 + sign * 10, at.AddSeconds(2));
            Check(s.Outcome is null && s.RiskFreeReachedAtUtc is null, "Legacy history must not silently acquire a new policy");
            s = New(); s.Status = "Pending"; s.EntryTriggeredAtUtc = null;
            ObservationOutcomeTracker.Expire(s, at);
            Check(s.Outcome == "Expired" && s.ExpiredAtUtc == at && s.Status == "Expired" && s.StopLossReachedAtUtc is null,
                "Unentered expiry is archived explicitly, not deleted or labeled stop");
        }
    }
    private static async Task PersistedPaperResult()
    {
        var root=Path.Combine(Path.GetTempPath(),"paper-result-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var old=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",Path.Combine(root,"main.json"));
            var runtime=new ShadowSignalRuntime();
            var features=new TechnicalFeatureSnapshot(.5m,1,0,0,0,1,1,.5m,.5m,1,.5m)
            {Wave=new(2,100,200,100,200,.8m,.8m,.8m,.1m,.1m,.1m,.8m,.1m,new decimal[8])
                {Rising=true,OrderedPath=Enumerable.Range(0,32).Select(i=>i/31m).ToArray()}};
            var at=DateTime.UtcNow;
            var candidate=new SignalCandidate("PAPERUSDT","15","Long",200,100,180,140,150,130,
                145,147.5m,10,1,0,200,100,100,200,100,"fixture",false,null);
            var analysis=new ProfessionalSignalAnalysis(features,new(null,null,0,0,0),"",[],[]){ObservedAtUtc=at};
            await runtime.AddAsync(candidate,"15",false,[],analysis,"owner",default);
            var s=(await runtime.Store.GetAllAsync()).Single();
            Check(s.ObservationPolicyVersion==1 && s.Status=="Pending","Only-approve policy survives the real store clone path");
            Check(s.BybitOrderId is null && s.SubmittedAtUtc is null && !File.Exists(Path.Combine(root,"main.json")),"Paper approval cannot register a live order or write the main queue");
            s.Status="Filled";s.EntryTriggeredAtUtc=at.AddSeconds(1);s.FilledAtUtc=s.EntryTriggeredAtUtc;
            await runtime.Store.UpdateAsync(s,default);
            ObservationOutcomeTracker.TrackFilled(s,147.5m,at.AddSeconds(2));await runtime.Store.UpdateAsync(s,default);
            var restart=new ServerOrderStore(Path.Combine(root,"shadow-signals.json"),Path.Combine(root,"shadow-history.db"));
            s=(await restart.GetAllAsync()).Single();
            Check(s.ObservationPolicyVersion==1 && s.RiskFreeReachedAtUtc==at.AddSeconds(2),"Restart preserves the active paper risk-free policy");
            ObservationOutcomeTracker.TrackFilled(s,145,at.AddSeconds(3));await restart.UpdateAsync(s,default);
            var history=await restart.GetLearningHistoryAsync(default);
            Check(history.Single().Signal.Outcome=="RiskFree","Paper risk-free closure reaches the learning archive");
            var patterns=SignalLearningService.BuildPatterns(history).Proposals;
            Check(patterns.Length==1 && patterns[0].Outcome=="RiskFree","Only-approve outcomes are included in proposal reference patterns");
            var query=new ServerSignal{Id=Guid.NewGuid(),Symbol="OTHERUSDT",Direction="Long",Timeframe="15",EntryPrice=140,TakeProfit=150,StopLoss=130,CreatedAtUtc=at.AddMinutes(1),TechnicalFeatures=features};
            var result=SignalSimilarityService.CalculateFromPatterns(query,patterns).Evidence!;
            Check(result.SameTimeframeNearbyRiskFree==1 && result.SameTimeframeNearbyTargets==0 && result.SameTimeframeNearbyStops==0,"The paper outcome contributes precisely to the risk-free count");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",old);
            SignalHistoryStore.ClearConnectionPools();Directory.Delete(root,true);
        }
    }

}

using Phoenix.Web;
using Microsoft.Extensions.Logging.Abstractions;
internal static class InAppQueueTests
{
    private static readonly TechnicalFeatureSnapshot Features=new(.5m,1m,0m,0m,0m,1m,1m,.5m,.5m,1m,.5m,
        EntryLevelStrength:.25m,StopLevelStrength:.5m,TargetLevelStrength:.75m,SimilarityContextVersion:3)
        {TradeGeometry=new(1,1,2,2,1)};
    public static void CapacityAndRestart()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var dir=Path.Combine(Path.GetTempPath(),"inapp-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        var previous=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",Path.Combine(dir,"queue.json"));
        try{
            var archive=new ReviewArchiveStore();
            SignalBatchService Service()=>new(null!,null!,null!,null!,null!,null!,null!,null!,null!,null!,NullLogger<SignalBatchService>.Instance,archive);
            var service=Service();
            var candidate=new SignalCandidate("TESTUSDT","15","Long",110,90,100,100,110,90,null,null,1,1,0,0,0,0,0,100,"fixture",false,null);
            var analysis=new ProfessionalSignalAnalysis(Features,new(null,null,0,0,0),"fixture",[],[]);
            for(int i=0;i<10;i++){
                await archive.SaveAsync("proposal"+i,candidate,[],"15",false,[1,2,3],default);
                await service.AddProposalAsync(new("proposal"+i,"owner",candidate,"15",false,analysis,"caption",[1,2,3],5,DateTime.UtcNow),default);
            }
            Check((await Service().PendingAsync("owner",default)).Count==10,"Queue must survive service restart");
            Check((await service.PendingAsync("other",default)).Count==0,"Owner isolation failed");
            var full=false;try{await service.AddProposalAsync(new("overflow","owner",candidate,"15",false,analysis,"caption",[],5,DateTime.UtcNow),default);}catch(InvalidOperationException){full=true;}
            Check(full,"Eleventh proposal must not be accepted");
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var waiting=service.WaitForCapacityAsync(20,null,cts.Token);await Task.Delay(100);Check(!waiting.IsCompleted,"Full queue must stop producer");
            Check(await service.DecideInAppAsync("other","proposal0","reject",null,default)!=null,"Another owner may not decide");
            var answers=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>service.DecideInAppAsync("owner","proposal0","reject","fixture reason",default)));
            Check(answers.Count(x=>x is null)==1,"Concurrent duplicate decision must be accepted only once");
            await waiting;Check((await Service().PendingAsync("owner",default)).Count==9,"One decision must release one slot durably");
            Check(await service.DecideInAppAsync("owner","proposal1","invalid",null,default)!=null,"Unknown action must not change queue");
            await service.AddProposalAsync(new("stuck","owner",candidate,"15",false,analysis,"caption",[],5,DateTime.UtcNow,"NeedsReview","failed"),default);
            Check(await service.RemoveProposalsAsync("other",new[]{"stuck"},default)==0,"Cannot remove another owner's proposal");
            Check(await service.RemoveProposalsAsync("owner",new[]{"stuck","proposal1"},default)==2,"Ready and stuck proposals must be removable");
            Check((await Service().PendingAsync("owner",default)).Count==8,"Removal must persist across restart");
            Check(await service.RemoveProposalsAsync("owner",new[]{"stuck"},default)==0,"Removal must be idempotent");
            Check(await Service().RestoreRemovedProposalsAsync("other",default)==0,"Restore must isolate owners");
            Check(await Service().RestoreRemovedProposalsAsync("owner",default)==2,"Last removal must be recoverable");
            Check((await service.PendingAsync("owner",default)).Single(x=>x.Key=="stuck").State=="NeedsReview","Restore must not replay an uncertain submission");
        }finally{Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",previous);SignalHistoryStore.ClearConnectionPools();Directory.Delete(dir,true);}
    }
    public static void AlertPolicy()
    {
        var now=DateTime.UtcNow;var options=new DedicatedTelegramOptions("other",null,null);
        var signal=new ServerSignal{Id=Guid.NewGuid(),RequestedByUsername="owner",CreatedAtUtc=now,CompletedAtUtc=now,Outcome="Expired"};
        string[] Kinds()=>MobileSignalWorker.Events(signal,"owner",options).Select(x=>x.Kind).ToArray();
        Check(Kinds().Length==0,"Registration and ordinary expiry must be silent");
        signal.ExpireAdjustedAtUtc=now.AddSeconds(-20);Check(Kinds().SequenceEqual(new[]{"NearEntry","Expired"}),"A previously near signal must announce expiry");
        signal.CompletedAtUtc=null;signal.EntryTriggeredAtUtc=now;Check(!Kinds().Contains("Opened"),"Internal entry trigger is not a fill");
        signal.FilledAtUtc=now;Check(Kinds().Contains("Opened"),"Exchange fill must announce entry");
        signal.CompletedAtUtc=now;signal.Outcome="Cancelled";Check(!Kinds().Contains("Cancelled"),"Cancellation must be silent");
    }
    public static void SimilarityRadius()
    {
        var signal=new ServerSignal{Direction="Long",Timeframe="15"};
        var moderate=TechnicalResemblance.Compare(signal,signal,Features,Features with{IchimokuPosition=.9m});
        Check(!moderate.Distances.ContainsKey("ichimoku") && moderate.Score==1 && moderate.Nearby,"Cloud indicators are completely absent from SR similarity");
        var distant=TechnicalResemblance.Compare(signal,signal,Features,Features with{SupportDistanceAtr=40,ResistanceDistanceAtr=40});
        Check(!distant.Nearby,"Strong geometry contradiction must remain excluded");
        Check(!TechnicalResemblance.Compare(signal,signal,Features,Features with{AtrPercent=0}).Comparable,"Missing data cannot invent a match");
    }
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}

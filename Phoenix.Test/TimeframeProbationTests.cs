using Phoenix.Web;
using System.Text.Json.Nodes;

internal static class TimeframeProbationTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    public static void SurvivorAndSequential()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var root=Path.Combine(Path.GetTempPath(),"timeframe-probation-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            foreach(var tf in new[]{"1","5","15","30","60","240","D","W","M","3M"})
            foreach(var survivorWins in new[]{true,false})
            {
                var queue=Path.Combine(root,tf+survivorWins+".json");var archive=queue+".db";
                var store=new ServerOrderStore(queue,archive);
                ServerSignal New(string symbol,string? timeframe=null)=>new(){Id=Guid.NewGuid(),Symbol=symbol,Direction="Long",Timeframe=timeframe??tf,Status="Pending",EntryPrice=100,TakeProfit=110,StopLoss=90,CreatedAtUtc=DateTime.UtcNow};
                async Task<ServerSignal> Add(string symbol){var s=New(symbol);await store.AddAsync(s);return s;}
                async Task End(Guid id,string outcome){var s=(await store.GetAllAsync()).Single(x=>x.Id==id);s.FilledAtUtc??=DateTime.UtcNow;s.CompletedAtUtc=DateTime.UtcNow;s.Status="Completed";s.Outcome=outcome;if(outcome=="StopLoss")s.StopLossReachedAtUtc=s.CompletedAtUtc;else s.TargetReachedAtUtc=s.CompletedAtUtc;await store.UpdateAsync(s);await store.UpdateAsync(s);}
                var first=await Add("BTCUSDT");var survivor=await Add("ETHUSDT");
                Check(await store.TryClaimSubmissionAsync(first.Id,100)&&await store.TryClaimSubmissionAsync(survivor.Id,100),tf+": normal capacity allows two reservations");
                await End(first.Id,"StopLoss");store=new ServerOrderStore(queue,archive);
                var state=(await store.TimeframeRulesAsync()).Single(x=>x.Rule.Timeframe==tf);
                Check(!state.Rule.Blocked&&state.EffectiveMaximumOpen==1&&state.Open==1&&state.AwaitingResult,tf+": first stop and survivor enforce one-slot mode across restart");
                var waiting=await Add("SOLUSDT");Check(!await store.TryClaimSubmissionAsync(waiting.Id,100),tf+": no replacement may refill the stop's slot while survivor is unresolved");
                Check((await store.GetAllAsync()).Single(x=>x.Id==waiting.Id).Status=="Pending",tf+": temporary wait keeps ordinary pending lifecycle");
                var other=New("OTHERUSDT",tf=="5"?"15":"5");await store.AddAsync(other);Check(await store.TryClaimSubmissionAsync(other.Id,100),tf+": probation is isolated by timeframe");
                var duringPauseAt=DateTime.UtcNow;
                await End(other.Id,"Target");
                Check((await store.TimeframeRulesAsync()).Single(x=>x.Rule.Timeframe==tf).Rule.ConsecutiveStops==1,tf+": another timeframe's target cannot reset this stop streak");
                await End(survivor.Id,survivorWins?"Target":"StopLoss");
                state=(await store.TimeframeRulesAsync()).Single(x=>x.Rule.Timeframe==tf);
                if(survivorWins){
                    Check(!state.Rule.Blocked&&state.Rule.ConsecutiveStops==0&&state.EffectiveMaximumOpen==2,tf+": survivor target restores two slots");
                    Check(!await store.TryClaimSubmissionAsync(waiting.Id,100,default,duringPauseAt),tf+": a tick from the waiting period cannot enter after target reset");
                    var next=await Add("ADAUSDT");var claims=await Task.WhenAll(store.TryClaimSubmissionAsync(waiting.Id,100),store.TryClaimSubmissionAsync(next.Id,100));Check(claims.All(x=>x),tf+": both slots may be used after target");
                }else{
                    Check(state.Rule.Blocked&&state.EffectiveMaximumOpen==0,tf+": survivor stop creates full lock");
                    Check(!await store.TryClaimSubmissionAsync(waiting.Id,100),tf+": full lock refuses waiting entry");
                    Check((await store.GetAllAsync()).Single(x=>x.Id==waiting.Id).ExpireReason=="TimeframeBlocked",tf+": full lock retains the prepared specific notification cause");
                }
            }
            // With no survivor, the next round starts with exactly one reservation, even under races.
            foreach(var wins in new[]{true,false}){
                var path=Path.Combine(root,"sequential-"+wins+".json");var store=new ServerOrderStore(path,path+".db");
                ServerSignal New(string symbol)=>new(){Id=Guid.NewGuid(),Symbol=symbol,Direction="Short",Timeframe="1",Status="Pending",EntryPrice=100,CreatedAtUtc=DateTime.UtcNow};
                async Task End(Guid id,string outcome){var s=(await store.GetAllAsync()).Single(x=>x.Id==id);s.Status="Completed";s.FilledAtUtc=DateTime.UtcNow;s.CompletedAtUtc=DateTime.UtcNow;s.Outcome=outcome;await store.UpdateAsync(s);}
                var previous=New("BTCUSDT");await store.AddAsync(previous);await store.TryClaimSubmissionAsync(previous.Id,100);await End(previous.Id,"StopLoss");
                var candidates=Enumerable.Range(0,12).Select(i=>New("COIN"+i+"USDT")).ToArray();foreach(var c in candidates)await store.AddAsync(c);
                var claimed=await Task.WhenAll(candidates.Select(c=>store.TryClaimSubmissionAsync(c.Id,100)));Check(claimed.Count(x=>x)==1,"After one stop and zero open trades, twelve concurrent attempts may reserve only one slot");
                var winner=candidates[Array.IndexOf(claimed,true)];await End(winner.Id,wins?"Target":"StopLoss");
                var state=(await store.TimeframeRulesAsync()).Single(x=>x.Rule.Timeframe=="1");Check(wins?state.EffectiveMaximumOpen==2&&!state.Rule.Blocked:state.Rule.Blocked&&state.EffectiveMaximumOpen==0,"Sequential next target resets; sequential next stop locks");
            }
            // A target-first pair must keep normal refill behavior while its second trade is open.
            {
                var path=Path.Combine(root,"target-first.json");var store=new ServerOrderStore(path,path+".db");
                ServerSignal New(string symbol)=>new(){Id=Guid.NewGuid(),Symbol=symbol,Direction="Long",Timeframe="60",Status="Pending",CreatedAtUtc=DateTime.UtcNow};
                var a=New("BTCUSDT");var b=New("ETHUSDT");var c=New("SOLUSDT");foreach(var s in new[]{a,b,c})await store.AddAsync(s);await store.TryClaimSubmissionAsync(a.Id,100);await store.TryClaimSubmissionAsync(b.Id,100);
                a=(await store.GetAllAsync()).Single(x=>x.Id==a.Id);a.FilledAtUtc=DateTime.UtcNow;a.CompletedAtUtc=DateTime.UtcNow;a.Status="Completed";a.Outcome="Target";await store.UpdateAsync(a);
                Check(await store.TryClaimSubmissionAsync(c.Id,100),"Target-first pair may refill the released slot while the survivor remains open");
            }
            // Larger user-configured caps cannot evade this safety policy, including legacy stored rules.
            {
                var path=Path.Combine(root,"legacy-cap.json");var store=new ServerOrderStore(path,path+".db");await store.ChangeTimeframeRuleAsync("15",4);
                var file=path+".timeframe-risk.json";var json=JsonNode.Parse(await File.ReadAllTextAsync(file))!;json["rules"]!["15"]!["consecutiveStops"]=2;json["rules"]!["15"]!["blocked"]=false;await File.WriteAllTextAsync(file,json.ToJsonString());
                store=new ServerOrderStore(path,path+".db");var state=(await store.TimeframeRulesAsync()).Single(x=>x.Rule.Timeframe=="15");Check(state.Rule.Blocked&&state.EffectiveMaximumOpen==0,"Legacy cap greater than two migrates into the two-stop brake");
                Check((await store.TimeframeLockNoticesAsync(DateTime.MinValue)).Count==1,"Policy upgrade records one lock notice");
            }
        }
        finally{SignalHistoryStore.ClearConnectionPools();Directory.Delete(root,true);}
    }
}

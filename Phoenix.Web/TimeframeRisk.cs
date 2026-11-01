using System.Text.Json;
using Phoenix.Engine.Exchanges.Bybit;
namespace Phoenix.Web;

public sealed record TimeframeRiskRule(string Timeframe, int MaximumOpen = 2, int ConsecutiveStops = 0,
    bool Blocked = false, DateTime? BlockedAtUtc = null, DateTime? ResumedAtUtc = null, DateTime? ProbationResolvedAtUtc = null);
public sealed record TimeframeStop(Guid SignalId, string Symbol);
public sealed record TimeframeLockNotice(Guid Id, string Timeframe, DateTime AtUtc, List<TimeframeStop> Stops);
public sealed record TimeframeRiskView(TimeframeRiskRule Rule, int Open, int EffectiveMaximumOpen = 2, bool AwaitingResult = false);
public sealed record TimeframeRiskChange(int MaximumOpen);
public sealed partial class ServerOrderStore
{
    private sealed class RiskState
    {
        public Dictionary<string,TimeframeRiskRule> Rules { get; set; } = new();
        public HashSet<Guid> Completed { get; set; } = new();
        public Dictionary<string,List<TimeframeStop>> StopStreaks { get; set; } = new();
        public List<TimeframeLockNotice> LockNotices { get; set; } = new();
    }
    private RiskState? _risk;
    private string RiskPath => _filePath + ".timeframe-risk.json";
    private static string RiskKey(ServerSignal s) => s.Timeframe ?? "unknown";
    private static bool OccupiesSlot(ServerSignal s) => s.CompletedAtUtc is null && s.Status is "Submitting" or "Submitted" or "Filled" or "Closing" or "RecoveryRequired";
    private async Task<RiskState> RiskUnsafeAsync(CancellationToken ct)
    {
        if(_risk is not null)return _risk;
        var state=_risk=File.Exists(RiskPath)
            ? JsonSerializer.Deserialize<RiskState>(await File.ReadAllTextAsync(RiskPath,ct),JsonOptions) ?? throw new InvalidDataException("قواعد تایم‌فریم قابل خواندن نیست.") : new();
        // A previously configured larger cap must not bypass the two-stop brake after upgrade.
        var changed=false;
        foreach(var (key,rule) in state.Rules.ToArray())if(!rule.Blocked&&rule.ConsecutiveStops>=2){
            var at=DateTime.UtcNow;var causes=await StopCausesUnsafeAsync(state,key,rule,ct);
            state.Rules[key]=rule with{Blocked=true,BlockedAtUtc=at};
            state.LockNotices.Add(new(Guid.NewGuid(),key,at,causes.ToList()));changed=true;
        }
        if(changed)await SaveRiskUnsafeAsync(ct);
        return state;
    }
    private static int EffectiveMaximum(TimeframeRiskRule rule) => rule.Blocked?0:
        rule.ConsecutiveStops>0?Math.Min(1,rule.MaximumOpen):rule.MaximumOpen;
    private async Task SaveRiskUnsafeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(RiskPath))!);
        await File.WriteAllTextAsync(RiskPath+".tmp",JsonSerializer.Serialize(_risk,JsonOptions),ct);
        File.Move(RiskPath+".tmp",RiskPath,true);
    }
    private static TimeframeRiskRule Rule(RiskState state,string key) => state.Rules.GetValueOrDefault(key) ?? new(key);
    private async Task RecordRiskCompletionUnsafeAsync(ServerSignal signal,CancellationToken ct)
    {
        if(signal.CompletedAtUtc is null || signal.FilledAtUtc is null)return;
        var state=await RiskUnsafeAsync(ct);if(!state.Completed.Add(signal.Id))return;
        var key=RiskKey(signal);var rule=Rule(state,key);
        var causes=await StopCausesUnsafeAsync(state,key,rule,ct);
        if(signal.Outcome=="StopLoss"){
            causes.Add(new(signal.Id,signal.Symbol));
            if(causes.Count>2)causes.RemoveRange(0,causes.Count-2);
        }else causes.Clear();
        var stops=signal.Outcome=="StopLoss" ? rule.ConsecutiveStops+1 : 0;
        var blocked=rule.Blocked || stops>=2;
        var at=rule.BlockedAtUtc??(blocked?DateTime.UtcNow:(DateTime?)null);
        state.Rules[key]=rule with {ConsecutiveStops=stops,Blocked=blocked,BlockedAtUtc=at,
            ProbationResolvedAtUtc=rule.ConsecutiveStops>0&&stops==0&&!rule.Blocked?DateTime.UtcNow:rule.ProbationResolvedAtUtc};
        if(!rule.Blocked&&blocked)state.LockNotices.Add(new(Guid.NewGuid(),key,at!.Value,causes.ToList()));
        await SaveRiskUnsafeAsync(ct);
    }
    private async Task<List<TimeframeStop>> StopCausesUnsafeAsync(RiskState state,string key,TimeframeRiskRule rule,CancellationToken ct)
    {
        if(state.StopStreaks.TryGetValue(key,out var causes))return causes;
        // Upgrade old risk files without guessing names or changing their existing lock policy.
        causes=new();
        if(rule.ConsecutiveStops>0){
            var known=(_signals??new List<ServerSignal>()).Concat(await _history.RiskStopSignalsAsync(key,ct))
                .Where(s=>state.Completed.Contains(s.Id)&&RiskKey(s)==key&&s.Outcome=="StopLoss")
                .DistinctBy(s=>s.Id).OrderByDescending(s=>s.CompletedAtUtc).Take(Math.Min(2,rule.ConsecutiveStops)).Reverse();
            causes.AddRange(known.Select(s=>new TimeframeStop(s.Id,s.Symbol)));
        }
        state.StopStreaks[key]=causes;return causes;
    }
    public async Task<IReadOnlyList<TimeframeLockNotice>> TimeframeLockNoticesAsync(DateTime since,CancellationToken ct=default)
    {
        await _gate.WaitAsync(ct);try{
            var state=await RiskUnsafeAsync(ct);
            return state.LockNotices.Where(n=>n.AtUtc>=since).Select(n=>n with{Stops=n.Stops.ToList()}).ToArray();
        }finally{_gate.Release();}
    }
    private async Task<bool> RiskAllowsUnsafeAsync(ServerSignal signal,List<ServerSignal> signals,decimal price,CancellationToken ct,DateTime? observedAtUtc=null)
    {
        if(observedAtUtc is {} received && received<signal.CreatedAtUtc)return false;
        var state=await RiskUnsafeAsync(ct);var key=RiskKey(signal);var rule=Rule(state,key);
        if(rule.Blocked || observedAtUtc is {} observed && rule.ResumedAtUtc is {} resumed && observed<=resumed)
        {
            signal.LastPrice=price;signal.EntryTriggeredAtUtc=DateTime.UtcNow;
            signal.Status=signal.Outcome="Expired";signal.ExpireReason="TimeframeBlocked";
            signal.CompletedAtUtc=signal.ExpiredAtUtc=DateTime.UtcNow;
            await SaveUnsafeAsync(signals,ct);await _history.UpsertAsync(signal,"TimeframeBlockedAtEntry",ct);return false;
        }
        // Do not replay an entry tick collected during probation after the survivor wins.
        if(observedAtUtc is {} entryAt&&rule.ProbationResolvedAtUtc is {} resolved&&entryAt<=resolved)return false;
        // Submitting reserves a slot before network calls. After one stop, the survivor
        // consumes the only allowed slot. Waiting signals keep the normal pending/expiry
        // lifecycle and must still satisfy a fresh entry observation when capacity returns.
        return signals.Count(x=>RiskKey(x)==key&&OccupiesSlot(x))<EffectiveMaximum(rule);
    }
    public async Task<IReadOnlyList<TimeframeRiskView>> TimeframeRulesAsync(CancellationToken ct=default)
    {
        await _gate.WaitAsync(ct);try{var signals=await LoadUnsafeAsync(ct);var state=await RiskUnsafeAsync(ct);
            return new[]{"1","5","15","30","60","240","D","W","M","3M"}.Concat(signals.Select(RiskKey)).Concat(state.Rules.Keys).Distinct()
                .Select(key=>{
                    var rule=Rule(state,key);var open=signals.Count(s=>RiskKey(s)==key&&OccupiesSlot(s));
                    return new TimeframeRiskView(rule,open,EffectiveMaximum(rule),!rule.Blocked&&rule.ConsecutiveStops>0&&open>=EffectiveMaximum(rule));
                }).ToArray();
        }finally{_gate.Release();}
    }
    public async Task ChangeTimeframeRuleAsync(string key,int maximum,CancellationToken ct=default)
    {
        if(maximum is <1 or >100 || !ValidRiskKey(key))throw new ArgumentException("تایم‌فریم یا سقف معتبر نیست.");
        await _gate.WaitAsync(ct);try{var state=await RiskUnsafeAsync(ct);var old=Rule(state,key);
            // Changing a limit must never silently release a stopped timeframe.
            var blocked=old.Blocked||old.ConsecutiveStops>=2;
            var at=old.BlockedAtUtc??(blocked?DateTime.UtcNow:(DateTime?)null);
            state.Rules[key]=old with{MaximumOpen=maximum,Blocked=blocked,BlockedAtUtc=at};
            if(!old.Blocked&&blocked)state.LockNotices.Add(new(Guid.NewGuid(),key,at!.Value,(await StopCausesUnsafeAsync(state,key,old,ct)).ToList()));
            await SaveRiskUnsafeAsync(ct);
        }finally{_gate.Release();}
    }
    public async Task ResumeTimeframeAsync(string key,IReadOnlyDictionary<string,decimal> currentPrices,CancellationToken ct=default)
    {
        if(!ValidRiskKey(key))throw new ArgumentException("تایم‌فریم معتبر نیست.");
        await _gate.WaitAsync(ct);try{var state=await RiskUnsafeAsync(ct);var old=Rule(state,key);if(!old.Blocked)return;
            var signals=await LoadUnsafeAsync(ct);
            foreach(var s in signals.Where(s=>RiskKey(s)==key&&s.Status=="Pending"&&s.CompletedAtUtc is null)){
                if(!currentPrices.TryGetValue(s.Symbol,out var price)||price<=0)throw new InvalidOperationException("ابتدا قیمت معتبر همه نمادها دریافت شود.");
                if(DemoOrderWorker.EntryReached(s,price))await RiskAllowsUnsafeAsync(s,signals,price,ct);
            }
            state.StopStreaks[key]=new();
            state.Rules[key]=old with{Blocked=false,ConsecutiveStops=0,BlockedAtUtc=null,ResumedAtUtc=DateTime.UtcNow};await SaveRiskUnsafeAsync(ct);
        }finally{_gate.Release();}
    }
    private static bool ValidRiskKey(string key)=>new[]{"1","5","15","30","60","240","D","W","M","3M","unknown"}.Contains(key);
}
public static class TimeframeRiskEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/signals/timeframe-rules",async(ServerOrderStore store,CancellationToken ct)=>Results.Ok(await store.TimeframeRulesAsync(ct)));
        app.MapPost("/api/signals/timeframe-rules/{timeframe}",async(string timeframe,TimeframeRiskChange input,HttpRequest request,ServerOrderStore store,CancellationToken ct)=>{
            if(!PhoenixSessionAuth.TryGetIdentity(request,out var identity)||!identity.IsAdmin)return Results.StatusCode(403);
            try{await store.ChangeTimeframeRuleAsync(timeframe,input.MaximumOpen,ct);return Results.Ok();}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        });
        app.MapPost("/api/signals/timeframe-rules/{timeframe}/resume",async(string timeframe,HttpRequest request,ServerOrderStore store,BybitDemoClient client,CancellationToken ct)=>{
            if(!PhoenixSessionAuth.TryGetIdentity(request,out var identity)||!identity.IsAdmin)return Results.StatusCode(403);
            await store.ExecutionGate.WaitAsync(ct);try{
                var prices=new Dictionary<string,decimal>();
                foreach(var symbol in (await store.GetAllAsync(ct)).Where(s=>(s.Timeframe??"unknown")==timeframe&&s.Status=="Pending"&&s.CompletedAtUtc is null).Select(s=>s.Symbol).Distinct())
                    prices[symbol]=(await client.GetLastPriceAsync(symbol,ct)).LastPrice;
                await store.ResumeTimeframeAsync(timeframe,prices,ct);return Results.Ok();
            }catch(Exception) when(!ct.IsCancellationRequested){return Results.BadRequest(new{error="آزادسازی انجام نشد؛ دریافت قیمت یا ذخیره قواعد را بررسی کن."});}finally{store.ExecutionGate.Release();}
        });
    }
}

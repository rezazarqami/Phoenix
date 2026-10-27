using System.Collections.Concurrent;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;
public sealed record WalletRecoveryStatus(string State,int Examined,int Recovered,int Unresolved,DateTime? AtUtc,string? Message);
public sealed record WalletRecoveryCandidate(DeviceSignalAlert Event,ServerSignal? Signal);
public sealed record WalletRecoveryMatch(decimal Balance,DateTime At,string OrderId,string TransactionId);

public sealed class BybitWalletRecovery(BybitDemoClient client,BybitDemoOptions options,MobileSignalFeed feed,
    ILogger<BybitWalletRecovery> logger):BackgroundService
{
    private readonly SemaphoreSlim recoveryGate=new(1,1);
    private readonly ConcurrentDictionary<string,WalletRecoveryStatus> states=new(StringComparer.OrdinalIgnoreCase);
    public WalletRecoveryStatus Status(string owner)=>states.TryGetValue(owner,out var s)?s:new("waiting",0,0,0,null,"بازیابی خودکار موجودی‌های ثبت‌نشده از بای‌بیت");
    public async Task<WalletRecoveryStatus> RecoverAsync(string owner,CancellationToken ct)
    {
        if(!await recoveryGate.WaitAsync(0,ct))return Status(owner);
        var examined=0;var recovered=0;var unresolved=0;var failures=0;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            states[owner]=new("running",0,0,0,DateTime.UtcNow,"در حال تطبیق موجودی نتایج با سوابق حساب…");
            var candidates=await feed.WalletRecoveryCandidatesAsync(owner,timeout.Token);examined=candidates.Count;
            foreach(var candidate in candidates.Where(c=>c.Signal?.ResultWalletBalanceUsdt is not null))
                if(await feed.SaveRecoveredWalletAsync(owner,candidate.Event.Id,candidate.Signal!.ResultWalletBalanceUsdt!.Value,
                    candidate.Signal.ResultWalletAtUtc??candidate.Event.At,"LocalSnapshot",candidate.Signal.Id.ToString(),"",timeout.Token))recovered++;
            var missing=candidates.Where(c=>c.Signal?.ResultWalletBalanceUsdt is null&&c.Event.WalletBalanceUsdt is null).ToArray();
            unresolved=missing.Length;
            if(!options.HasCredentials&&missing.Length>0)return states[owner]=new("unavailable",examined,recovered,unresolved,DateTime.UtcNow,"دسترسی حساب بای‌بیت روی سرور تنظیم نشده است.");
            foreach(var day in missing.Where(c=>c.Signal?.FilledAtUtc is not null&&c.Event.At>=DateTime.UtcNow.AddYears(-2).AddDays(1)).GroupBy(c=>c.Event.At.Date))
            {
                var from=DateTime.SpecifyKind(day.Key,DateTimeKind.Utc).AddMinutes(-15);var to=from.AddDays(1).AddMinutes(30);if(to>DateTime.UtcNow)to=DateTime.UtcNow;
                try
                {
                    // Both endpoints are read-only. Do not hold journal or trading locks across network calls.
                    var closed=await client.GetClosedTradesAsync(from,to,timeout.Token);
                    var transactions=await client.GetCashTransactionsAsync(from,to,timeout.Token);
                    foreach(var candidate in day)
                    {
                        var match=Match(candidate,closed,transactions,candidates);
                        if(match is not null&&await feed.SaveRecoveredWalletAsync(owner,candidate.Event.Id,match.Balance,match.At,"BybitTransaction",match.OrderId,match.TransactionId,timeout.Token)){recovered++;unresolved--;}
                    }
                }
                catch(OperationCanceledException) when(timeout.IsCancellationRequested){throw;}
                catch(Exception ex){failures++;logger.LogWarning(ex,"Historical wallet recovery could not read a Bybit day");}
            }
            return states[owner]=new(failures>0?"partial":"completed",examined,recovered,unresolved,DateTime.UtcNow,
                failures>0?"بخشی از سوابق بای‌بیت دریافت نشد؛ دسترسی API و ارتباط با صرافی را بررسی کن.":unresolved>0?"برای بعضی نتایج، تراکنش کامل و قابل تطبیق پیدا نشد؛ موجودی حدس زده نمی‌شود.":"بازیابی موجودی‌ها انجام شد.");
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){states[owner]=new("partial",examined,recovered,unresolved,DateTime.UtcNow,"بازیابی متوقف شد؛ مقادیر ثبت‌شده محفوظ‌اند.");throw;}
        catch(Exception ex){logger.LogWarning(ex,"Historical wallet recovery stopped");return states[owner]=new("partial",examined,recovered,unresolved,DateTime.UtcNow,"بازیابی کامل نشد؛ نتایج ذخیره‌شده محفوظ‌اند. دوباره تلاش کن.");}
        finally{recoveryGate.Release();}
    }
    public static WalletRecoveryMatch? Match(WalletRecoveryCandidate candidate,IReadOnlyList<BybitClosedTrade> closed,
        IReadOnlyList<BybitCashTransaction> transactions,IReadOnlyList<WalletRecoveryCandidate> candidates)
    {
        var s=candidate.Signal;if(s?.FilledAtUtc is null)return null;
        var matches=closed.Where(c=>MatchesClose(candidate,c)).ToArray();if(matches.Length!=1)return null;var close=matches[0];
        if(candidates.Count(other=>MatchesClose(other,close))!=1)return null;
        var rows=transactions.Where(t=>t.OrderId==close.OrderId&&t.Symbol==close.Symbol&&t.Side==close.Side&&t.Type=="TRADE").ToArray();
        if(rows.Length==0||rows.Any(t=>t.Quantity is null or <=0m||t.CashBalance is null||t.At<s.FilledAtUtc.Value||Math.Abs((t.At-close.At).TotalMinutes)>15))return null;
        if(rows.GroupBy(t=>t.Id).Any(g=>g.Count()>1)||rows.Sum(t=>t.Quantity!.Value)!=close.ClosedSize)return null;
        var final=rows.Where(t=>t.RemainingSize==0m).ToArray();if(final.Length!=1||rows.Any(t=>t.At>final[0].At))return null;
        // At identical timestamps, position size=0 establishes the final fill without guessing transaction-ID order.
        return new(final[0].CashBalance!.Value,final[0].At,close.OrderId,final[0].Id);
    }
    private static bool MatchesClose(WalletRecoveryCandidate candidate,BybitClosedTrade close)
    {
        var s=candidate.Signal;if(s?.FilledAtUtc is null||s.Direction is not ("Long" or "Short"))return false;
        var quantity=s.ExecutedQuantity??s.Quantity;var entry=s.AverageFillPrice??s.EntryPrice;
        if(quantity<=0||entry<=0||close.ClosedSize!=quantity||close.Symbol!=s.Symbol||close.Side!=(s.Direction=="Long"?"Sell":"Buy")||
            close.At<s.FilledAtUtc.Value||Math.Abs((close.At-candidate.Event.At).TotalMinutes)>15)return false;
        if(candidate.Event.Kind=="RiskFree"&&!string.IsNullOrWhiteSpace(s.RiskFreeCloseOrderId))return close.OrderId==s.RiskFreeCloseOrderId;
        return close.EntryPrice.HasValue&&Math.Abs(close.EntryPrice.Value-entry)<=Math.Max(entry*0.00000001m,0.00000001m);
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5),ct);
        while(!ct.IsCancellationRequested)
        {
            if(options.HasCredentials&&PhoenixSessionAuth.CredentialsConfigured(out var owner,out _))await RecoverAsync(owner,ct);
            await Task.Delay(TimeSpan.FromMinutes(30),ct);
        }
    }
}

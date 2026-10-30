namespace Phoenix.Web;

public sealed record ConnectionCheck(string Status, DateTime? CheckedAtUtc, DateTime? LastSuccessAtUtc,
    double? AgeSeconds, string? Error);
public sealed record ExchangeConnectionSnapshot(string Status, string Reason, ConnectionCheck PublicApi,
    ConnectionCheck Account, ConnectionCheck Monitoring, int AffectedSymbolCount, IReadOnlyList<string> AffectedSymbols);

// All readings come from completed checks, with explicit expiry. A price or
// monitoring exception never substitutes for the independent API/account probe.
public sealed class ServerState
{
    private readonly object gate = new();
    private sealed record Probe(bool? Ok = null, DateTime? At = null, DateTime? Success = null, string? Error = null);
    private Probe publicApi = new(), account = new(), monitoring = new();
    private readonly Dictionary<string,string> symbolErrors = new(StringComparer.OrdinalIgnoreCase);
    private decimal? lastPrice;
    private DateTime? priceAt;
    public decimal? LastPrice { get { lock(gate) return lastPrice; } }
    public DateTime? LastUpdatedUtc { get { lock(gate) return priceAt; } }
    public bool PublicApiConnected => Read(true).PublicApi.Status == "connected";
    public bool DemoAuthenticated => Read(true).Account.Status == "connected";
    public string? Error { get { lock(gate) return publicApi.Error ?? account.Error ?? monitoring.Error ?? symbolErrors.Values.FirstOrDefault(); } }
    public void RecordPrice(decimal price, DateTime at) { lock(gate) { lastPrice=price;priceAt=at; } }
    public void RecordPublic(bool ok, string? error, DateTime at) { lock(gate) publicApi=Update(publicApi,ok,error,at); }
    public void RecordAccount(bool ok, string? error, DateTime at) { lock(gate) account=Update(account,ok,error,at); }
    public void BeginMonitoring(IEnumerable<string> symbols)
    {
        lock(gate) { var active=symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var key in symbolErrors.Keys.Where(k=>!active.Contains(k)).ToArray())symbolErrors.Remove(key); }
    }
    public void RecordSymbol(string symbol, string? error)
    { lock(gate) { if(error is null)symbolErrors.Remove(symbol);else symbolErrors[symbol]=error; } }
    public void RecordMonitoring(bool ok, string? error, DateTime at)
    { lock(gate) monitoring=Update(monitoring,ok,error,at); }
    private static Probe Update(Probe old,bool ok,string? error,DateTime at)=>new(ok,at,ok?at:old.Success,ok?null:error);

    public ExchangeConnectionSnapshot Read(bool credentialsConfigured, DateTime? at = null)
    {
        var now=at??DateTime.UtcNow;
        lock(gate)
        {
            ConnectionCheck Check(Probe p,int maxAge,bool configured=true)
            {
                var age=p.At is {} time?Math.Max(0,(now-time).TotalSeconds):(double?)null;
                var status=!configured?"not_configured":p.At is null?"checking":age>maxAge?"stale":p.Ok==true?"connected":"error";
                return new(status,p.At,p.Success,age,p.Error);
            }
            var pub=Check(publicApi,45); var acc=Check(account,90,credentialsConfigured); var mon=Check(monitoring,120);
            var status=pub.Status=="error"||acc.Status=="error"||mon.Status=="error"?"error":
                pub.Status!="connected"||acc.Status!="connected"||mon.Status!="connected"?"unknown":
                symbolErrors.Count>0?"degraded":"connected";
            var reason=pub.Status=="error"?"دسترسی عمومی بای‌بیت ناموفق است":
                acc.Status=="error"?"دسترسی حساب بای‌بیت ناموفق است":
                mon.Status=="error"?"پایش نمادها ناموفق است":
                acc.Status=="not_configured"?"کلید حساب تنظیم نشده است":
                pub.Status is "checking" or "stale"?"وضعیت عمومی بای‌بیت هنوز تأیید نشده یا قدیمی است":
                acc.Status is "checking" or "stale"?"وضعیت حساب هنوز تأیید نشده یا قدیمی است":
                mon.Status!="connected"?"پایش قیمت‌ها تأیید نشده یا به‌روز نیست":
                symbolErrors.Count>0?"اختلال جزئی در پایش بعضی نمادها":"دسترسی عمومی، حساب و پایش اخیر تأیید شده‌اند";
            return new(status,reason,pub,acc,mon,symbolErrors.Count,symbolErrors.Keys.Order().Take(20).ToArray());
        }
    }
}

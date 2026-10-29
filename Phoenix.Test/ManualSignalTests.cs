using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;
internal static class ManualSignalTests
{
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    public static void Plans()
    {
        var rules=new BybitInstrumentRules("BTCUSDT",.01m,.001m,.001m,5m,100m,1m,.01m);
        foreach(var direction in new[]{"Long","Short"})
        {
            var d=direction=="Long"?1m:-1m;
            var request=new ManualSignalRequest(Guid.NewGuid(),"BTCUSDT",direction,"15",100m,100m+d*10m,100m-d*5m,100m+d*20m,10m,10m);
            var signal=ManualSignals.Build(request,rules);
            Check(signal.Id==request.RequestId&&signal.Origin==ManualSignals.Origin,"Stable identity and manual origin");
            Check(signal.EntryPrice==request.EntryPrice&&signal.TakeProfit==request.TakeProfit&&signal.StopLoss==request.StopLoss&&signal.ExpirePrice==request.ExpirePrice,"User levels must not be recalculated from ceiling/floor");
            signal.ApplyPhoenixLeverage(rules);
            Check(signal.Quantity==1m&&signal.Leverage==10m&&signal.LeverageSource=="Manual","Margin times explicit leverage determines quantity");
            Check(signal.ExpireActivationPrice==100m+d*2.5m&&signal.RiskFreePrice==100m+d*7.5m&&signal.StopLoss2==100m+d*5m&&signal.EarlyStopLimit==100m+d*2m,"Existing 25%, 50/20%, 75/50/20% lifecycle levels");
            Check((signal.TakeProfit-signal.EntryPrice)*signal.Quantity*d==10m&&(signal.StopLoss-signal.EntryPrice)*signal.Quantity*d==-5m,"Long and Short dollar PnL");
            Check((request with{StopLoss=request.TakeProfit}).Validate()!=null,"Reject a stop on the profit side");
            Check((request with{ExpirePrice=request.StopLoss}).Validate()!=null,"Reject expiry on wrong side");
            bool rejected=false;try{ManualSignals.Build(request with{Leverage=101},rules);}catch(ArgumentException){rejected=true;}Check(rejected,"Never silently cap user leverage");
            rejected=false;try{ManualSignals.Build(request with{TakeProfit=100m+d*.001m},rules);}catch(ArgumentException){rejected=true;}Check(rejected,"Reject rounded coincident entry and target");
        }
    }
    public static void Archive()=>ArchiveAsync().GetAwaiter().GetResult();
    private static async Task ArchiveAsync()
    {
        var dir=Path.Combine(Path.GetTempPath(),"manual-signals-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        try
        {
            var queue=Path.Combine(dir,"queue.json");var history=Path.Combine(dir,"history.db");var store=new ServerOrderStore(queue,history);
            var rules=new BybitInstrumentRules("BTCUSDT",.01m,.001m,.001m,5m,100m);
            var signal=ManualSignals.Build(new(Guid.NewGuid(),"BTCUSDT","Long","15",100,110,95,120,10,10),rules);
            await store.AddAsync(signal);var copy=(await store.GetAllAsync()).Single();Check(copy.Origin==ManualSignals.Origin&&copy.LeverageSource=="Manual","Queue clones retain manual provenance and sizing");
            copy.Status="Expired";copy.Outcome="Expired";copy.ExpireReason="InitialBoundary";copy.CompletedAtUtc=copy.ExpiredAtUtc=DateTime.UtcNow;await store.UpdateAsync(copy);
            var automatic=ManualSignals.Build(new(Guid.NewGuid(),"BTCUSDT","Long","15",100,110,95,120,10,10),rules);automatic.Origin=null;await store.AddAsync(automatic);
            var restarted=new ServerOrderStore(queue,history);var report=await restarted.GetManualReportAsync(0);
            Check(report.Total==1&&report.Expired==1&&report.Active==0&&report.Rows.Single().Signal.Symbol=="BTCUSDT","Manual initial expiries survive compaction and automatic records stay out of report");
            Check(report.Rows.Single().Signal.Leverage==10&&report.Rows.Single().Signal.EntryPrice==100,"History retains chosen levels and leverage after restart");
        }
        finally{SignalHistoryStore.ClearConnectionPools();Directory.Delete(dir,true);}
    }
}

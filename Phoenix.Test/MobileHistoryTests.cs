using Phoenix.Web;
internal static class MobileHistoryTests
{
    public static void History()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify(){
        var folder=Path.Combine(Path.GetTempPath(),"history-"+Guid.NewGuid());Directory.CreateDirectory(folder);
        var old=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");var path=Path.Combine(folder,"signals.json");Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",path);
        try{
            var store=new ServerOrderStore(path,Path.Combine(folder,"history.db"));var options=new DedicatedTelegramOptions("dedicated",null,null);
            var at=DateTime.UtcNow.AddDays(-40);Guid focused=Guid.Empty;
            for(int i=0;i<60;i++){
                var s=new ServerSignal{Id=Guid.NewGuid(),Symbol="BTCUSDT",Direction="Long",RequestedByUsername="owner",Status="Completed",CreatedAtUtc=at.AddMinutes(i),FilledAtUtc=at.AddMinutes(i),CompletedAtUtc=at.AddMinutes(i).AddSeconds(10),Outcome="Target",EntryPrice=100,TakeProfit=110,StopLoss=90};
                if(i==10)focused=s.Id;await store.AddAsync(s);
            }
            await store.AddAsync(new ServerSignal{Id=Guid.NewGuid(),RequestedByUsername="other",Symbol="PRIVATEUSDT",CreatedAtUtc=at,FilledAtUtc=at});
            var feed=new MobileSignalFeed(store,options);
            var current=await feed.HistoryAsync("owner",null,null,default);Check(current.Events.Count==50&&current.HasMore,"Page size and older cursor");
            Check(current.Events.All(x=>x.Symbol=="BTCUSDT"&&x.EntryPrice==100),"Private rows must not leak and levels must be retained");
            var results=await feed.HistoryAsync("owner",null,null,default,"results");Check(results.Events.Count==50&&results.HasMore&&results.Events.All(e=>e.Kind=="Target"),"Result filtering must happen before the 50-row page limit");
            var oldResults=await feed.HistoryAsync("owner",results.Before,null,default,"results");Check(oldResults.Events.Count==10&&!oldResults.HasMore,"Older result page must contain the remaining final outcomes, not intermediate events");
            var hiddenFocus=await feed.HistoryAsync("owner",null,focused+":Opened",default,"results");Check(!hiddenFocus.FocusFound,"A non-result deep link must not enter results-only view");
            var older=await feed.HistoryAsync("owner",current.Before,null,default);Check(!older.Events.Select(x=>x.Id).Intersect(current.Events.Select(x=>x.Id)).Any(),"Pages must not overlap");
            var selected=await feed.HistoryAsync("owner",null,focused+":Target",default);Check(selected.FocusFound&&selected.Events.Last().Id==focused+":Target","Deep link must include the selected old event");
            var isolated=await feed.HistoryAsync("other",null,focused+":Target",default);Check(!isolated.FocusFound&&!isolated.Events.Any(x=>x.Symbol=="BTCUSDT"),"Deep links must not cross accounts");
            var restarted=await new MobileSignalFeed(store,options).HistoryAsync("owner",null,null,default);Check(restarted.Events.Select(x=>x.Id).SequenceEqual(current.Events.Select(x=>x.Id)),"History must survive restart without duplicates");
            var live=await feed.ReadAsync("owner",null,default);Check(live.Events.Count==0,"Opening old history must not replay phone alerts");
        }finally{Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",old);SignalHistoryStore.ClearConnectionPools();Directory.Delete(folder,true);}
    }
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}

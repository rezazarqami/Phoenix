using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;
using System.Net;
using System.Text.Json;
internal static class SelectedSignalTests
{
 public static void Cancellation()=>Verify().GetAwaiter().GetResult();
 private static async Task Verify()
 {
  var root=Path.Combine(Path.GetTempPath(),"selected-signals-"+Guid.NewGuid());Directory.CreateDirectory(root);
  try{
   var store=new ServerOrderStore(Path.Combine(root,"queue.json"),Path.Combine(root,"history.db"));
   ServerSignal Signal(string status)=>new(){Id=Guid.NewGuid(),Symbol="BTCUSDT",Direction="Long",Status=status,CreatedAtUtc=DateTime.UtcNow,EntryPrice=100,TakeProfit=110,StopLoss=90,Ceiling=120,Floor=80};
   var pending=Signal("Pending");var filled=Signal("Filled");filled.FilledAtUtc=DateTime.UtcNow;var sending=Signal("Submitting");var unselected=Signal("Pending");var partial=Signal("Submitted");partial.BybitOrderId="partial";var next=Signal("Pending");
   foreach(var s in new[]{pending,filled,sending,unselected,partial,next})await store.AddAsync(s);
   var requests=new List<string>();var client=new BybitDemoClient(new("fixture","fixture"),new HttpClient(new Handler(request=>{
    requests.Add(request.Method+" "+request.RequestUri!.AbsolutePath);Check(!request.RequestUri.AbsolutePath.EndsWith("create"),"Selected cancellation must never place or close positions");
    var body=request.Method==HttpMethod.Post?"""{"retCode":0,"result":{"orderId":"partial","orderLinkId":""}}""":"""{"retCode":0,"result":{"list":[{"orderId":"partial","symbol":"BTCUSDT","orderStatus":"PartiallyFilled","cumExecQty":"0.5","avgPrice":"100","updatedTime":"1793060000000"}]}}""";
    return new(HttpStatusCode.OK){Content=new StringContent(body)};
   })));
   var result=await new SignalCancellationService(store,client).CancelSelectedAsync([pending.Id,pending.Id,filled.Id,sending.Id,partial.Id,next.Id]);
   Check(result.Count==5&&result.Count(r=>r.Cancelled)==2,"Distinct selected pending signals must cancel, independently of ineligible IDs");
   var saved=await store.GetAllAsync();Check(saved.Single(s=>s.Id==unselected.Id).Status=="Pending","Unselected waiting signals must remain unchanged");Check(saved.Single(s=>s.Id==filled.Id).Status=="Filled"&&saved.Single(s=>s.Id==sending.Id).Status=="Submitting","Filled and submitting records must remain untouched");Check(saved.Single(s=>s.Id==partial.Id).Status=="Submitted","A partial execution must not be reported as cancelled");Check(requests.Count==2,"Only the submitted exchange order should require cancellation and verification calls");
   await store.RemoveAsync(pending.Id);var archived=await store.FindSignalAsync(pending.Id);Check(archived is not null&&archived.Ceiling==120&&archived.EntryPrice==100&&archived.TakeProfit==110&&archived.StopLoss==90,"Live chart lookup must retain the exact archived signal levels");Check(await store.FindSignalAsync(Guid.NewGuid()) is null,"Unknown chart IDs must not resolve to an unrelated signal");
  }finally{SignalHistoryStore.ClearConnectionPools();Directory.Delete(root,true);}
 }
 private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> handler):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(handler(request));}
 private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}

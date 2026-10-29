using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
internal static class SinglePositionCloseTests
{
 public static void Close()=>Verify().GetAwaiter().GetResult();
 private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 private static async Task Verify(){
  var path=Path.Combine(Path.GetTempPath(),"single-close-"+Guid.NewGuid());Directory.CreateDirectory(path);var sent=new List<JsonElement>();bool closed=false;
  var options=new BybitDemoOptions("fixture","fixture");
  var client=new BybitDemoClient(options,new HttpClient(new Handler(async request=>{
   string body;
   if(request.RequestUri!.AbsolutePath.Contains("/order/create")){sent.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone());closed=true;body="""{"retCode":0,"result":{"orderId":"close-one"}}""";}
   else if(request.RequestUri.AbsolutePath.Contains("wallet-balance"))body="""{"retCode":0,"result":{"list":[{"coin":[{"coin":"USDT","walletBalance":"123"}]}]}}""";
   else body=closed?"""{"retCode":0,"result":{"list":[{"symbol":"BTCUSDT","side":"Sell","positionIdx":2,"size":"2","avgPrice":"100"}]}}""":"""{"retCode":0,"result":{"list":[{"symbol":"BTCUSDT","side":"Buy","positionIdx":1,"size":"1","avgPrice":"100","positionIM":"10","leverage":"10"},{"symbol":"BTCUSDT","side":"Sell","positionIdx":2,"size":"2","avgPrice":"100"}]}}""";
   return new(HttpStatusCode.OK){Content=new StringContent(body)};
  })));
  try{
   var store=new ServerOrderStore(Path.Combine(path,"queue.json"),Path.Combine(path,"history.db"));
   using var service=new BulkPositionService(store,client,options,NullLogger<BulkPositionService>.Instance);
   var preview=await service.PreviewAsync(default,"BTCUSDT","Buy",1,"owner");Check(preview.Positions.Count==1&&preview.Single,"Preview must isolate symbol, hedge side and position index");
   var outcome=await service.CloseAsync(preview.Id,default);Check(outcome.Single().Submitted,"Submit the selected close");var order=sent.Single();Check(order.GetProperty("reduceOnly").GetBoolean()&&order.GetProperty("side").GetString()=="Sell"&&order.GetProperty("positionIdx").GetInt32()==1&&order.GetProperty("qty").GetString()=="1","Close must reduce only the selected side and current approved size");
   Check((await store.GetAllAsync()).Single().Status=="Closing","Exchange submission is not confirmation of closure");
   await service.StartAsync(default);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));while((await store.GetAllAsync()).Single().Outcome!="ManualClosed")await Task.Delay(20,timeout.Token);await service.StopAsync(default);
   var result=(await store.GetAllAsync()).Single();Check(result.Id!=Guid.Empty&&result.RequestedByUsername=="owner"&&result.Outcome=="ManualClosed","Even an external position gets a durable attributable manual-close audit");
   Check(sent.Count==1,"Opposite hedge side must remain untouched");bool replay=false;try{await service.CloseAsync(preview.Id,default);}catch(InvalidOperationException){replay=true;}Check(replay&&sent.Count==1,"Consumed preview must not close twice");
  }finally{SignalHistoryStore.ClearConnectionPools();Directory.Delete(path,true);}
 }
 private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request);}
}

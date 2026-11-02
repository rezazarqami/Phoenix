using System.Net;
using System.Text.Json;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Web;

internal static class Strategy2RecoveryTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run() => Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2-recovery-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            foreach (var mode in new[] { "flat", "position", "order-page-two", "position-race", "network-error", "cursor-loop" })
            {
                var file = Path.Combine(root, mode + ".json");
                var db = Path.Combine(root, mode + ".db");
                var store = new ServerOrderStore(file, db);
                var pending = new ServerSignal { Id=Guid.NewGuid(), Symbol="BTCUSDT", Direction="Long", Status="RecoveryRequired", RecoveryOriginalStatus="Pending", CreatedAtUtc=DateTime.UtcNow.AddDays(-5), OrderLinkId="old-pending" };
                var filled = new ServerSignal { Id=Guid.NewGuid(), Symbol="ETHUSDT", Direction="Long", Status="RecoveryRequired", RecoveryOriginalStatus="Filled", CreatedAtUtc=DateTime.UtcNow.AddDays(-5), BybitOrderId="old-fill", FilledAtUtc=DateTime.UtcNow.AddDays(-5), EntryPrice=100, TakeProfit=110, StopLoss=90 };
                var backlog = new ServerSignal { Id=Guid.NewGuid(), Symbol="SOLUSDT", Status="Pending", CreatedAtUtc=DateTime.UtcNow };
                await store.AddAsync(pending); await store.AddAsync(filled); await store.AddAsync(backlog);
                int positionCalls=0, orderCalls=0, requests=0;
                var client = new BybitDemoClient(new("fixture","fixture"),new HttpClient(new Handler(r =>
                {
                    requests++;
                    Check(r.Method==HttpMethod.Get,"Recovery must never place or cancel exchange orders");
                    Check(r.Headers.Contains("X-BAPI-SIGN"),"Exchange checks must be authenticated");
                    if(mode=="network-error")throw new HttpRequestException("offline");
                    if(r.RequestUri!.AbsolutePath=="/v5/position/list")
                    {
                        positionCalls++;
                        var live = mode=="position" || mode=="position-race" && positionCalls==2;
                        return Json(new {retCode=0,result=new{list=live ? new[]{new{symbol="ETHUSDT",side="Buy",positionIdx=0,size="1"}} : [],nextPageCursor=""}});
                    }
                    Check(r.RequestUri.AbsolutePath=="/v5/order/realtime","Only account-wide open order checks are used");
                    Check(r.RequestUri.Query.Contains("openOnly=0"),"Do not mistake absent historical orders for an empty account");
                    orderCalls++;
                    var active = mode=="order-page-two" && orderCalls==2;
                    var cursor = mode=="cursor-loop" || mode=="order-page-two" && orderCalls==1 ? "second page" : "";
                    if(orderCalls==2)Check(r.RequestUri.Query.Contains("cursor=second%20page"),"Follow every open-order page");
                    return Json(new{retCode=0,result=new{list=active ? new[]{new{orderId="still-open",symbol="ETHUSDT",orderStatus="New"}} : [],nextPageCursor=cursor}});
                })));
                bool failed=false;
                try { await new Strategy2Recovery(store,client).ReconcileAsync(default); }
                catch(Exception) when(mode is "network-error" or "cursor-loop") { failed=true; }
                var rows=await store.GetAllAsync();
                if(mode=="flat")
                {
                    Check(!store.EntriesPaused&&positionCalls==2&&orderCalls==1,"Resume only after complete empty-order and repeated flat-position checks");
                    Check(rows.Single(s=>s.Id==pending.Id).Status=="Cancelled","Never replay a restored pending order");
                    Check(rows.Single(s=>s.Id==backlog.Id).Status=="Cancelled","Do not submit stale signals accumulated during the pause");
                    var resolved=rows.Single(s=>s.Id==filled.Id);
                    Check(resolved.Status=="Completed"&&resolved.Outcome=="RecoveryResolved"&&resolved.TargetReachedAtUtc is null&&resolved.StopLossReachedAtUtc is null,"Do not invent a historical TP/SL result");
                    Check(resolved.RecoveryOriginalStatus=="Filled"&&resolved.RecoveryResolvedAtUtc.HasValue&&resolved.BybitOrderId=="old-fill","Preserve recovery audit and exchange IDs");
                    var restart=new ServerOrderStore(file,db);Check(!(await restart.GetAllAsync()).Any(s=>s.Status=="RecoveryRequired"),"Recovery must persist across restarts");
                    var callsBefore=requests;await new Strategy2Recovery(store,client).ReconcileAsync(default);Check(requests==callsBefore,"Resolved recovery must be idempotent and make no exchange calls");
                    var fresh=new ServerSignal{Id=Guid.NewGuid(),Symbol="NEWUSDT",Status="Pending",CreatedAtUtc=DateTime.UtcNow};await store.AddAsync(fresh);
                    Check(await store.TryClaimExclusiveSubmissionAsync(fresh.Id,100,default)==ExclusiveClaimResult.Claimed,"New signals can claim entry after recovery");
                }
                else
                {
                    Check(store.EntriesPaused&&rows.Count(s=>s.Status=="RecoveryRequired")==2&&rows.Single(s=>s.Id==backlog.Id).Status=="Pending","Any open order, position, race or failed check must preserve the pause and queue");
                    Check(failed==(mode is "network-error" or "cursor-loop"),"Failed checks must propagate, never masquerade as an empty account");
                }
            }
        }
        finally { SignalHistoryStore.ClearConnectionPools();Directory.Delete(root,true); }
    }
    private static HttpResponseMessage Json(object data)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(data))};
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(respond(request));
    }
}

using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

internal static class BybitWalletRecoveryTests
{
    public static void Matching()
    {
        var at=DateTime.UtcNow.AddMinutes(-5);var id=Guid.NewGuid();
        var s=new ServerSignal{Id=id,Symbol="BTCUSDT",Direction="Long",EntryPrice=100,AverageFillPrice=100,Quantity=2,ExecutedQuantity=2,FilledAtUtc=at.AddMinutes(-10),CompletedAtUtc=at,Outcome="Target"};
        var e=new DeviceSignalAlert(1,id+":Target","Target",at,"BTCUSDT","Long",null);
        var candidate=new WalletRecoveryCandidate(e,s);var close=new BybitClosedTrade("close","BTCUSDT","Sell",at,100,2);
        var rows=new[]{new BybitCashTransaction("first","close","BTCUSDT","Sell","TRADE",at.AddMilliseconds(-1),1,1,29),new BybitCashTransaction("last","close","BTCUSDT","Sell","TRADE",at,1,0,30)};
        var found=BybitWalletRecovery.Match(candidate,[close],rows,[candidate]);Check(found?.Balance==30&&found.TransactionId=="last","The final fill's actual cash balance must be used");
        Check(BybitWalletRecovery.Match(candidate,[close],rows.Take(1).ToArray(),[candidate]) is null,"Incomplete fills must not be used");
        Check(BybitWalletRecovery.Match(candidate,[close,close with{OrderId="another"}],rows,[candidate]) is null,"Ambiguous closing orders must stay unresolved");
        Check(BybitWalletRecovery.Match(candidate,[close],rows,[candidate,new(e with{Id=Guid.NewGuid()+":Target"},s)]) is null,"One exchange close must not be assigned to two Phoenix outcomes");
        Check(BybitWalletRecovery.Match(candidate,[close with{Side="Buy"}],rows,[candidate]) is null,"Entry/exit side must not be reversed");
        Check(BybitWalletRecovery.Match(candidate,[close],rows.Select(t=>t with{RemainingSize=1}).ToArray(),[candidate]) is null,"A partially open position must not be treated as fully closed");
        Check(BybitWalletRecovery.Match(candidate,[close],rows.Select(t=>t with{CashBalance=null}).ToArray(),[candidate]) is null,"Missing wallet balance must never become zero");
        var tied=rows.Select(t=>t with{At=at}).ToArray();Check(BybitWalletRecovery.Match(candidate,[close],tied,[candidate])?.Balance==30,"Position size zero identifies the final fill even with tied timestamps");
        var shortSignal=new ServerSignal{Id=id,Symbol="BTCUSDT",Direction="Short",EntryPrice=100,Quantity=2,FilledAtUtc=s.FilledAtUtc,RiskFreeCloseOrderId="close"};
        var shortCandidate=new WalletRecoveryCandidate(e with{Kind="RiskFree",Direction="Short"},shortSignal);
        Check(BybitWalletRecovery.Match(shortCandidate,[close with{Side="Buy"}],rows.Select(t=>t with{Side="Buy",CashBalance=0}).ToArray(),[shortCandidate])?.Balance==0,"Short risk-free closures and zero balances must work");
        shortSignal.RiskFreeCloseOrderId="different";Check(BybitWalletRecovery.Match(shortCandidate,[close with{Side="Buy"}],rows,[shortCandidate]) is null,"Known risk-free order ID must be honored");
    }
    public static void Persistence()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var root=Path.Combine(Path.GetTempPath(),"bybit-wallet-"+Guid.NewGuid());Directory.CreateDirectory(root);var old=Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH");var path=Path.Combine(root,"queue.json");Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",path);
        try
        {
            var at=DateTime.UtcNow.AddMinutes(-5);var store=new ServerOrderStore(path,Path.Combine(root,"history.db"));
            var signal=new ServerSignal{Id=Guid.NewGuid(),Symbol="BTCUSDT",Direction="Long",RequestedByUsername="owner",Status="Completed",CreatedAtUtc=at.AddMinutes(-10),FilledAtUtc=at.AddMinutes(-9),CompletedAtUtc=at,Outcome="Target",Quantity=2,ExecutedQuantity=2,AverageFillPrice=100,EntryPrice=100,TakeProfit=110,StopLoss=90};await store.AddAsync(signal);await store.RemoveAsync(signal.Id);
            var calls=0;var txPages=0;var options=new BybitDemoOptions("fixture","fixture");
            var client=new BybitDemoClient(options,new HttpClient(new Handler(request=>
            {
                Check(request.Method==HttpMethod.Get,"Recovery must not send exchange trading requests");calls++;
                Check(request.Headers.Contains("X-BAPI-SIGN"),"Historical requests must be signed");
                object data;
                if(request.RequestUri!.AbsolutePath.EndsWith("closed-pnl"))data=new{retCode=0,result=new{list=new[]{new{orderId="close",symbol="BTCUSDT",side="Sell",execType="Trade",updatedTime=new DateTimeOffset(at).ToUnixTimeMilliseconds().ToString(),avgEntryPrice="100",closedSize="2"}},nextPageCursor=""}};
                else
                {
                    Check(request.RequestUri.AbsolutePath.EndsWith("transaction-log"),"No current-wallet lookup may be used for a historical result");txPages++;
                    if(txPages==2)Check(request.RequestUri.Query.Contains("cursor=second%20page"),"Historical pagination cursor must be encoded");
                    data=new{retCode=0,result=new{list=new[]{new{id=txPages==1?"first":"last",orderId="close",symbol="BTCUSDT",side="Sell",type="TRADE",currency="USDT",category="linear",transactionTime=new DateTimeOffset(at).ToUnixTimeMilliseconds().ToString(),qty="1",size=txPages==1?"1":"0",cashBalance=txPages==1?"29":"30.25"}},nextPageCursor=txPages==1?"second page":""}};
                }
                return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(data))};
            })));
            var feed=new MobileSignalFeed(store,new("dedicated",null,null));using var recovery=new BybitWalletRecovery(client,options,feed,NullLogger<BybitWalletRecovery>.Instance);
            var result=await recovery.RecoverAsync("owner",default);Check(result.Recovered==1&&result.Unresolved==0&&calls==3,"Archived result must recover from all transaction pages");
            var saved=(await new MobileSignalFeed(store,new("dedicated",null,null)).HistoryAsync("owner",null,null,default,"results")).Events.Single();
            Check(saved.WalletBalanceUsdt==30.25m&&saved.WalletSource=="BybitTransaction"&&saved.WalletAtUtc.HasValue,"Balance, source and actual settlement time must persist across restart");
            var second=await recovery.RecoverAsync("owner",default);Check(second.Recovered==0&&calls==3,"Repeated recovery must leave existing snapshots untouched and avoid exchange requests");
            Check(!await feed.SaveRecoveredWalletAsync("other",saved.Id,888,at,"BybitTransaction","close","last",default),"Recovered balances must remain owner-scoped");
            var local=new ServerSignal{Id=Guid.NewGuid(),Symbol="LOCALUSDT",Direction="Long",RequestedByUsername="owner",Status="Completed",CreatedAtUtc=at.AddDays(-10),FilledAtUtc=at.AddDays(-10),CompletedAtUtc=at.AddDays(-10),Outcome="StopLoss",ResultWalletBalanceUsdt=0,ResultWalletAtUtc=at};await store.AddAsync(local);
            // Journal initialization already happened, so an old stored snapshot must be restored explicitly.
            var localResult=await recovery.RecoverAsync("owner",default);Check(localResult.Recovered==1&&calls==3,"Existing local wallet snapshots must be preferred over any exchange work");
            var locals=await feed.HistoryAsync("owner",null,null,default,"results");Check(locals.Events.Single(e=>e.Symbol=="LOCALUSDT").WalletBalanceUsdt==0,"Local zero balance must stay zero");
        }
        finally{Environment.SetEnvironmentVariable("PHOENIX_QUEUE_PATH",old);SignalHistoryStore.ClearConnectionPools();Directory.Delete(root,true);}
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(send(request));}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenix.Web;

internal static class RiskFreeExitMessageTests
{
    public static void AllExitTypes()=>Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var bodies=new List<string>();
        var notifier=new PublicSignalNotifier(new("fake-token","fake-chat"),NullLogger<PublicSignalNotifier>.Instance,
            new HttpClient(new Handler(request=>{
                var raw=request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if(request.RequestUri!.AbsolutePath.EndsWith("sendMessage"))
                {using var json=JsonDocument.Parse(raw);bodies.Add(json.RootElement.GetProperty("text").GetString()!);}
                else bodies.Add(raw);
                return new(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"result\":{\"message_id\":123}}")};
            })));
        foreach(var (kind,title,type) in new[]{("FirstStepLimit","پلهٔ اول ریسک‌فری","استاپ‌لیمیت"),
            ("SecondStepLimit","پلهٔ دوم ریسک‌فری","استاپ‌لیمیت"),("SafetyStopMarket","محافظ ریسک‌فری","استاپ‌مارکت")})
        {
            var s=new ServerSignal{Symbol="BTCUSDT",PublicTelegramMessageId=1,RiskFreeCloseKind=kind};
            await notifier.RiskFreeClosedAsync(s,default);
            await notifier.RiskFreeClosedAsync(s,[137,80,78,71],true,default);
            if(!bodies.TakeLast(2).All(b=>b.Contains(title) && b.Contains(type)))
                throw new Exception("Incorrect risk-free text/photo label: "+kind);
            if(kind!="SafetyStopMarket" && bodies.TakeLast(2).Any(b=>b.Contains("محافظ ریسک‌فری")))
                throw new Exception("Limit closure was mislabeled as the safety market");
        }
        await notifier.RiskFreeClosedAsync(new ServerSignal{Symbol="OLDUSDT",PublicTelegramMessageId=1,ProtectionStage=2},default);
        if(!bodies.Last().Contains("ثبت نشده") || bodies.Last().Contains("با محافظ"))
            throw new Exception("Historical closure without filled order evidence must not be guessed from stage");
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(send(request));}
}

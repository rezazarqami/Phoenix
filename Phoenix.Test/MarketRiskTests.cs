using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenix.Web;

internal static class MarketRiskTests
{
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    public static void Run() => Verify().GetAwaiter().GetResult();
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(respond(request)); }
    sealed class Factory(Func<HttpRequestMessage,HttpResponseMessage> respond) : IHttpClientFactory
    { public HttpClient CreateClient(string name)=>new(new Handler(respond)); }
    static async Task Verify()
    {
        var history=Enumerable.Repeat(1d,120).ToArray();
        Check(MarketRiskMath.Score(1,history)==0,"Median is normal");
        Check(MarketRiskMath.Score(2,history)>=90,"A single extreme must reach danger");
        Check(MarketRiskMath.Score(.1,history,true)>=90,"Falling depth increases danger");
        Check(MarketRiskMath.Score(50,history.Take(59).ToArray())==null,"Warmup cannot claim safety");
        Check(MarketRiskMath.Score(double.NaN,history)==null,"Invalid observations remain unknown");
        RiskMetric M(int? score,string state="live")=>new("BTC","BTC","%",1,1,score,120,DateTimeOffset.UtcNow,state);
        Check(MarketRiskMath.Overall([M(95),M(0),M(0),M(0)]).State=="danger","Do not average away an extreme");
        Check(MarketRiskMath.Overall([M(75),M(72)]).State=="danger","Two elevated indicators trigger danger");
        Check(MarketRiskMath.Overall([M(5),M(null,"missing")]).State=="partial","Missing data cannot look normal");
        Check(MarketRiskMath.Overall([M(null,"missing")]).Score==null,"No observations means unknown");
        var ics="BEGIN:VCALENDAR\r\nX-WR-TIMEZONE:America/New_York\r\nBEGIN:VEVENT\r\nUID:jan\r\nSUMMARY:Consumer Price "+"\r\n Index\\, release\r\nDTSTART;TZID=America/New_York:20260115T083000\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:july\r\nSUMMARY:Employment Situation\r\nDTSTART;TZID=America/New_York:20260715T083000\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:unknown\r\nSUMMARY:Consumer Price Index\r\nDTSTART;VALUE=DATE:20260715\r\nEND:VEVENT\r\nEND:VCALENDAR";
        var events=MarketRiskCalendar.ParseIcs(ics,"BLS","https://www.bls.gov");
        Check(events.Length==2&&events[0].Title.Contains("Index, release"),"Unfold ICS lines and never invent all-day release times");
        Check(events[0].At.Hour==13&&events[1].At.Hour==12,"Eastern daylight savings must convert correctly");
        var fed=MarketRiskCalendar.ParseFed("{\"events\":[{\"title\":\"FOMC Minutes\",\"description\":\"&lt;p&gt;Meeting&lt;/p&gt;\",\"month\":\"2026-11\",\"days\":\"18\",\"time\":\"2:00 p.m.\"},{}]}");
        Check(fed.Length==1&&fed[0].At.Hour==19&&!fed[0].Title.Contains('<'),"Parse official Fed feed and clean markup");
        var directory=Path.Combine(Path.GetTempPath(),"phoenix-risk-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var previous=Environment.GetEnvironmentVariable("PHOENIX_MARKET_RISK_PATH");var path=Path.Combine(directory,"observer.json");Environment.SetEnvironmentVariable("PHOENIX_MARKET_RISK_PATH",path);
        var calls=new List<string>();
        var factory=new Factory(request=>{
            Check(request.Method==HttpMethod.Get,"Observer must only issue public GETs");
            var uri=request.RequestUri!;lock(calls)calls.Add(uri.AbsoluteUri);
            object result;
            if(uri.Host!="api.bybit.com")
            {
                var content=uri.Host=="www.federalreserve.gov"?"{\"events\":[{\"title\":\"FOMC Minutes\",\"month\":\"2027-01\",\"days\":\"18\",\"time\":\"2:00 p.m.\"}]}":ics;
                return new(HttpStatusCode.OK){Content=new StringContent(content)};
            }
            var symbol=uri.Query.Contains("BTCUSDT")?"BTCUSDT":"ETHUSDT";
            if(uri.AbsolutePath.EndsWith("kline"))
            {
                var start=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/60000*60000;
                var rows=Enumerable.Range(0,122).Select(i=>new[]{(start-i*60000).ToString(),"100","101","99",i==0?"103":"100.01","1","1000"}).ToArray();
                result=new{symbol,list=rows};
            }
            else result=new{s=symbol,ts=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),b=new[]{new[]{"100","10"}},a=new[]{new[]{"100.01","10"}}};
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{retCode=0,result}))};
        });
        try
        {
            var observer=new MarketRiskObserver(factory,NullLogger<MarketRiskObserver>.Instance);
            await observer.StartAsync(default);
            var timeout=DateTimeOffset.UtcNow.AddSeconds(5);
            while(observer.Snapshot().Metrics.Length<8&&DateTimeOffset.UtcNow<timeout)await Task.Delay(20);
            var snapshot=observer.Snapshot();
            Check(snapshot.Metrics.Length==8&&snapshot.ObservationOnly,"All metrics run independently of execution");
            Check(snapshot.State=="danger"&&snapshot.Alerts.Any(a=>a.Kind=="market"),"Correlated price shocks create an observer alert");
            Check(snapshot.Metrics.Where(m=>m.Key.EndsWith("-spread")||m.Key.EndsWith("-depth")).All(m=>m.State=="calibrating"),"New book feeds need warmup");
            Check(snapshot.Metrics.Single(m=>m.Key=="BTCUSDT-volume").Baseline==1000,"Exclude the current volume candle from its baseline");
            await observer.StopAsync(default);observer.Dispose();
            var archive=JsonSerializer.Deserialize<MarketRiskObserver.Archive>(File.ReadAllText(path))!;
            Check(archive.Alerts.Any(a=>a.Kind=="market")&&archive.Samples.Count>0,"Alert history and calibration survive restart");
            Check(calls.All(c=>!c.Contains("/order/")&&!c.Contains("/position/")),"No private trading APIs can be touched");
            Check(typeof(MarketRiskObserver).GetConstructors().Single().GetParameters().All(p=>p.ParameterType==typeof(IHttpClientFactory)||p.ParameterType==typeof(Microsoft.Extensions.Logging.ILogger<MarketRiskObserver>)),"Observer has no trading dependency");
            var stale=DateTimeOffset.UtcNow.AddMinutes(-2);
            var staleFactory=new Factory(r=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{retCode=0,result=new{s="BTCUSDT",symbol="BTCUSDT",ts=stale.ToUnixTimeMilliseconds(),b=new[]{new[]{"100","10"}},a=new[]{new[]{"101","10"}},list=Array.Empty<string[]>()}}))});
            var missing=new MarketRiskObserver(staleFactory,NullLogger<MarketRiskObserver>.Instance);await missing.StartAsync(default);
            timeout=DateTimeOffset.UtcNow.AddSeconds(5);while(missing.Snapshot().Metrics.Length<8&&DateTimeOffset.UtcNow<timeout)await Task.Delay(20);
            Check(missing.Snapshot().State=="unavailable"&&missing.Snapshot().Score==null,"Stale or empty exchange responses must never produce a green score");await missing.StopAsync(default);missing.Dispose();
        }
        finally{Environment.SetEnvironmentVariable("PHOENIX_MARKET_RISK_PATH",previous);Directory.Delete(directory,true);}
    }
}

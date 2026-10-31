using System.Net;
using System.Text.Json;
using Phoenix.Web;
using Phoenix.Engine.Exchanges.Bybit;

internal static class TechnicalCohortTests
{
    private static void Check(bool x,string message){if(!x)throw new Exception(message);}
    public static void MembershipAndGeometry()
    {
        var at=new DateTime(2026,10,15,0,0,0,DateTimeKind.Utc);
        var f=new TechnicalFeatureSnapshot(.6m,1m,.3m,0,0,1,1,.5m,.5m,1.3m,.5m,SimilarityContextVersion:3) {Wave=new(1,1,2,90,110,.8m,.8m,.8m,.1m,.1m,.1m,.8m,.1m,new decimal[8])};
        var candidate=new ServerSignal { Direction="Long",Timeframe="5",EntryPrice=100,TakeProfit=110,StopLoss=90,CreatedAtUtc=at,TechnicalFeatures=f };
        LearnedSignalPattern Row(int days,string direction="Long",string tf="5",decimal target=110,decimal stop=90)=>new(new ServerSignal {
            Id=Guid.NewGuid(),Symbol="TEST",Direction=direction,Timeframe=tf,EntryPrice=100,TakeProfit=target,StopLoss=stop,
            EntryTriggeredAtUtc=at.AddDays(-days),CompletedAtUtc=at.AddDays(-days).AddHours(1)
        },days%2==0?"Target":"StopLoss",f with{Wave=f.Wave! with{Start=days}});
        var rows=new[]{Row(1),Row(2),Row(40),Row(3,"Short","5",90,110),Row(4,"Long","15"),Row(5,target:130),Row(6,stop:99)};
        var c=TechnicalCohorts.Build(candidate,rows,rows.Select(x=>(x,1d)).ToArray());
        Check(c.Count==3 && c.Targets==2 && c.Stops==1,"Direction, timeframe, RR and risk distance must all match");
        Check(c.BaselineTargets+c.BaselineStops==5,"Baseline must use same direction and timeframe before geometry filtering");
        Check(c.DistinctDays==3 && c.DistinctSymbols==1 && c.EffectiveSamples==3,"Do not confuse many rows with market diversity");
        Check(c.RecentTargets==1 && c.RecentStops==1 && c.TargetRate is null,"Recent window is as-of query and small cohorts have no rate");
        Check(c.Conditions.Count==3 && c.Conditions[0].Targets==2,"Conditions describe the same strict cohort, not winning-only selection");
        Check(!TechnicalCohorts.SimilarGeometry(candidate,new ServerSignal()),"Unknown geometry must not match");
        var future=Row(-1);var all=Enumerable.Range(1,12).Select(i=>Row(i)).Append(future).ToArray();
        var result=SignalSimilarityService.CalculateFromPatterns(candidate,all);
        Check(result.Evidence?.SameTimeframeNearbyCount==12,"Future labels must not enter technical support");
        var json=JsonSerializer.Serialize(result.Evidence);
        Check(JsonSerializer.Deserialize<SimilarityEvidence>(json)?.SameTimeframeNearbyCount==12,"Expanded support must survive persistence");
    }
    public static void ExactAtrGeometry()
    {
        var candles=Enumerable.Range(0,60).Select(i=>new BybitKline(i*60_000L,100,101,99,100,10)).ToArray();
        var candidate=new SignalCandidate("TEST","5","Long",110,90,100,100,106,96,null,null,1,1,0,0,0,0,0,60,"",false,null);
        var f=TechnicalFeatureExtractor.Calculate(candles,candidate);
        Check(f.TradeGeometry is { AtrPrice:2,StopDistanceAtr:2,TargetDistanceAtr:3,RewardRisk:1.5m,EntryDistanceAtr:0 },"Store exact price-unit ATR geometry at observation");
        var shortFeatures=TechnicalFeatureExtractor.Calculate(candles,candidate with { Direction="Short",TakeProfit=94,StopLoss=104 });
        Check(shortFeatures.TradeGeometry?.RewardRisk==1.5m,"Short geometry must normalize correctly");
        Check(TechnicalFeatureExtractor.Calculate(candles,candidate with { StopLoss=101 }).TradeGeometry is null,"Invalid stops must not become plausible absolute distances");
        var old=JsonSerializer.Serialize(f with { TradeGeometry=null });
        Check(JsonSerializer.Deserialize<TechnicalFeatureSnapshot>(old)?.TradeGeometry is null,"Legacy missing geometry remains missing");
    }
    public static void LongTelegramEvidence()
    {
        var longText="<b>نمونه‌های نزدیک</b>\n"+new string('ش',3490)+"🟢\n"+new string('د',800);
        var chunks=TelegramPhotoDelivery.PlainChunks(longText);
        Check(chunks.All(x=>x.Length<=3500) && string.Concat(chunks)==longText.Replace("<b>","").Replace("</b>",""),"Never truncate evidence or split Unicode incorrectly");
        foreach(var fail in new[]{0,1,2})
        {
            var handler=new Handler(fail);using var client=new HttpClient(handler);
            using var body=new MultipartFormDataContent();
            body.Add(new StringContent("test-chat"),"chat_id");body.Add(new StringContent(longText),"caption");
            body.Add(new StringContent("{\"inline_keyboard\":[]}"),"reply_markup");body.Add(new ByteArrayContent([1,2]),"photo","test.png");
            using var response=TelegramPhotoDelivery.SendAsync(client,"https://example.invalid/sendPhoto",body,default).GetAwaiter().GetResult();
            Check(response.IsSuccessStatusCode==(fail==0),"Partial delivery failure must not report success");
            Check(!handler.Bodies[0].Contains("inline_keyboard") && !handler.Bodies[0].Contains(new string('ش',50)),"Long report and action controls must leave the photo caption together");
            if(fail==0)Check(handler.Bodies.Count==3 && !handler.Bodies[1].Contains("reply_markup") && handler.Bodies[2].Contains("reply_markup") && handler.Bodies[2].Contains("reply_parameters"),"Only complete final report gets action controls linked to its image");
        }
    }
    private sealed class Handler(int fail):HttpMessageHandler
    {
        public List<string> Bodies {get;}=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return new(Bodies.Count==fail?HttpStatusCode.BadRequest:HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"result\":{\"message_id\":12}}")};
        }
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Phoenix.Web;

public static class MobilePushTests
{
    public static void SignalEvents()
    {
        var now = DateTime.UtcNow;
        var dedicated = new DedicatedTelegramOptions("arman", null, null);
        var signal = new ServerSignal { Id=Guid.NewGuid(), RequestedByUsername="owner", CreatedAtUtc=now, FilledAtUtc=now.AddSeconds(1), RiskFreeReachedAtUtc=now.AddSeconds(2), CompletedAtUtc=now.AddSeconds(3), Outcome="Target" };
        var events = MobileSignalWorker.Events(signal,"owner",dedicated).ToArray();
        Check(events.Select(e=>e.Kind).SequenceEqual(new[]{"Created","Opened","RiskFreeReached","Target"}),"Missing signal lifecycle events.");
        Check(events.Select(e=>e.Id).Distinct().Count()==4,"Lifecycle ids must be stable and distinct.");
        Check(events.SequenceEqual(MobileSignalWorker.Events(signal,"owner",dedicated)),"Re-reading signals changed event ids.");
        signal.RequestedByUsername="editor";
        Check(!MobileSignalWorker.Events(signal,"owner",dedicated).Any(),"Another user's private signal leaked.");
        signal.PublicSignalNumber=123;
        Check(MobileSignalWorker.Events(signal,"owner",dedicated).Count()==4,"Public events must not depend on Telegram delivery.");
        signal.RequestedByUsername="arman";
        Check(!MobileSignalWorker.Events(signal,"owner",dedicated).Any(),"Dedicated private route leaked.");
    }
    public static void DurableDelivery()
    {
        var folder=Path.Combine(Path.GetTempPath(),"mobile-push-"+Guid.NewGuid());Directory.CreateDirectory(folder);
        var oldDb=Environment.GetEnvironmentVariable("PERSONAL_DATABASE_PATH");var oldAccount=Environment.GetEnvironmentVariable("PHOENIX_FIREBASE_SERVICE_ACCOUNT");
        var a=Guid.NewGuid();var b=Guid.NewGuid();var calls=new List<(string Token,string Event)>();
        var handler=new Handler(request=>{
            if(request.RequestUri!.Host=="oauth2.googleapis.com")return Response(HttpStatusCode.OK,"{\"access_token\":\"test-access\",\"expires_in\":3600}");
            Check(request.RequestUri.Host=="fcm.googleapis.com","Unexpected delivery host.");
            using var doc=JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());var message=doc.RootElement.GetProperty("message");
            var token=message.GetProperty("token").GetString()!;var data=message.GetProperty("data");var id=data.GetProperty("eventId").GetString()!;
            Check(data.GetProperty("destination").GetString()=="/phoenix","Signal destination escaped the allowlist.");
            Check(data.GetProperty("eventKind").GetString()=="Created","Missing event kind.");
            Check(!message.TryGetProperty("notification",out _),"Private fields must not become background notification content.");
            calls.Add((token,id));
            return token.StartsWith("token-a")&&calls.Count(x=>x.Token==token)==1
                ?Response(HttpStatusCode.ServiceUnavailable,"{\"error\":{\"status\":\"UNAVAILABLE\"}}")
                :Response(HttpStatusCode.OK,"{\"name\":\"projects/test-project/messages/1\"}");
        });
        try
        {
            Environment.SetEnvironmentVariable("PERSONAL_DATABASE_PATH",Path.Combine(folder,"personal.db"));
            using var rsa=RSA.Create(2048);var account=Path.Combine(folder,"service-account.json");
            File.WriteAllText(account,JsonSerializer.Serialize(new{project_id="test-project",client_email="test@example.invalid",private_key=rsa.ExportPkcs8PrivateKeyPem()}));
            Environment.SetEnvironmentVariable("PHOENIX_FIREBASE_SERVICE_ACCOUNT",account);
            var factory=new Factory(new HttpClient(handler));var service=new MobilePushService(factory);
            service.RegisterAsync("owner",new(a,"token-a-12345678901234567890"),default).GetAwaiter().GetResult();
            service.RegisterAsync("owner",new(b,"token-b-12345678901234567890"),default).GetAwaiter().GetResult();
            var at=DateTime.UtcNow.AddSeconds(1);var now=new DateTimeOffset(at.AddSeconds(1));
            var e=new MobileSignalEvent(Guid.NewGuid()+":Created","Created",at);
            service.QueueAsync("someone-else",new[]{e},now,default).GetAwaiter().GetResult();
            service.DeliverPendingAsync(now,default).GetAwaiter().GetResult();Check(calls.Count==0,"Owner isolation failed.");
            service.QueueAsync("owner",new[]{e,e with{Id="old-event",At=at.AddHours(-1)},e with{Id="pre-enrollment",At=at.AddMinutes(-1)}},now,default).GetAwaiter().GetResult();
            service.QueueAsync("owner",new[]{e},now,default).GetAwaiter().GetResult();
            service.DeliverPendingAsync(now,default).GetAwaiter().GetResult();Check(calls.Count==2,"Each enrolled device must have one initial delivery.");
            service.DeliverPendingAsync(now.AddSeconds(1),default).GetAwaiter().GetResult();Check(calls.Count==2,"Backoff or sent-device dedup failed.");
            service=new MobilePushService(factory);
            service.DeliverPendingAsync(now.AddSeconds(6),default).GetAwaiter().GetResult();Check(calls.Count==3,"Retry did not survive service restart.");
            Check(calls.All(c=>c.Event==e.Id),"A retry changed its event id or replayed history.");
            service.QueueAsync("owner",new[]{e},now.AddSeconds(7),default).GetAwaiter().GetResult();
            service.DeliverPendingAsync(now.AddSeconds(7),default).GetAwaiter().GetResult();Check(calls.Count==3,"Repeated scan redelivered an acknowledged event.");
            var next=e with{Id=Guid.NewGuid()+":Created",At=at.AddSeconds(10)};
            service.QueueAsync("owner",new[]{next},now.AddSeconds(11),default).GetAwaiter().GetResult();
            service.RemoveAsync("owner",a,default).GetAwaiter().GetResult();
            service.DeliverPendingAsync(now.AddSeconds(12),default).GetAwaiter().GetResult();Check(calls.Count==4&&calls[^1].Token.StartsWith("token-b"),"A disabled device received a queued event.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERSONAL_DATABASE_PATH",oldDb);Environment.SetEnvironmentVariable("PHOENIX_FIREBASE_SERVICE_ACCOUNT",oldAccount);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);
        }
    }
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static HttpResponseMessage Response(HttpStatusCode code,string body)=>new(code){Content=new StringContent(body)};
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> action):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(action(request));}
    sealed class Factory(HttpClient client):IHttpClientFactory{public HttpClient CreateClient(string name)=>client;}
}

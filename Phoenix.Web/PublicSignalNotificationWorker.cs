using System.Text.Json;

namespace Phoenix.Web;

// Persisted activation + terminal snapshots are the outbox. Delivery acknowledgments
// live separately and can never overwrite a trading status, fill or result.
public sealed class PublicSignalNotificationWorker(
    ServerOrderStore store, PublicSignalNotifier notifier,
    EntrySignalReviewService entryReview,
    ILogger<PublicSignalNotificationWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim cycleGate=new(1,1);
    public sealed class Ledger
    {
        public DateTime SinceUtc { get; set; } = DateTime.UtcNow;
        public HashSet<string> Sent { get; set; } = [];
        public Dictionary<string,int> Publications { get; set; } = [];
        public Dictionary<string,DateTime> RetryAfterUtc { get; set; } = [];
    }

    internal static DateTime? PublicationAt(ServerSignal signal)=>
        signal.ExpireAdjustedAtUtc ?? signal.EntryTriggeredAtUtc;

    public static IEnumerable<(string Kind, DateTime At)> Events(ServerSignal s) => Events(s, false);
    public static IEnumerable<(string Kind, DateTime At)> Events(ServerSignal s, bool dedicatedRoute)
    {
        if (!dedicatedRoute && s.PublicTelegramMessageId is not > 0) yield break;
        if (s.FilledAtUtc is { } opened) yield return ("Opened", opened);
        if (s.RiskFreeReachedAtUtc is { } activated) yield return ("RiskFreeReached", activated);
        if (s.CompletedAtUtc is not { } ended) yield break;
        if (s.Outcome is "Target" or "StopLoss" or "RiskFree") yield return (s.Outcome, ended);
        if (s.Outcome == "Expired" && (dedicatedRoute || s.ExpireReason == "TargetAfterActivation"))
            yield return ("Expired", ended);
        if (dedicatedRoute && s.Outcome is "Cancelled" or "ManualClosed") yield return (s.Outcome, ended);
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        Ledger? ledger=null;
        while(!token.IsCancellationRequested)
        {
            try
            {
                if(ledger is null)
                {
                    ledger=File.Exists(store.NotificationLedgerPath)
                        ? JsonSerializer.Deserialize<Ledger>(await File.ReadAllTextAsync(store.NotificationLedgerPath,token))
                            ?? throw new InvalidDataException("Invalid public notification ledger.")
                        : new Ledger{SinceUtc=System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()};
                    await SaveAsync(store.NotificationLedgerPath,ledger,token);
                }
                await RunCycleAsync(ledger,token);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning(ex,"Public signal delivery cycle failed");}
            try{await Task.Delay(TimeSpan.FromSeconds(5),token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
        }
    }

    internal async Task RunCycleAsync(Ledger ledger,CancellationToken token=default)
    {
        await cycleGate.WaitAsync(token);
        try
        {
            foreach(var signal in await store.NotificationSignalsAsync(ledger.SinceUtc,token))
            {
                var publicationAt=PublicationAt(signal);
                var legacyPublication=signal.PublicTelegramMessageId is >0 &&
                    !ledger.Publications.Keys.Any(key=>key.StartsWith(signal.Id+":",StringComparison.Ordinal));
                foreach(var destination in notifier.Destinations(signal))
                {
                    try
                    {
                        var rootKey=$"{signal.Id}:{destination.Key}";
                        var sender=destination.Notifier;
                        if(ledger.RetryAfterUtc.TryGetValue(destination.Key,out var retryAt) && retryAt>DateTime.UtcNow)continue;
                        ledger.RetryAfterUtc.Remove(destination.Key);
                        if(!ledger.Publications.ContainsKey(rootKey) && legacyPublication)
                        {
                            ledger.Publications[rootKey]=signal.PublicTelegramMessageId!.Value;
                            await SaveAsync(store.NotificationLedgerPath,ledger,token);
                        }
                        if(!ledger.Publications.ContainsKey(rootKey) && publicationAt is { } activation &&
                            (activation>=ledger.SinceUtc || signal.CompletedAtUtc is null))
                        {
                            signal.PublicSignalNumber??=await store.ReservePublicSignalNumberAsync(signal.Id,token);
                            byte[]? chart=null;var currentImage=false;
                            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                                try{chart=(await entryReview.BuildCurrentAsync(signal,timeout.Token)).Image;currentImage=true;}
                                catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                                catch(Exception ex){logger.LogWarning(ex,"Public chart unavailable for {SignalId}; using saved image/text",signal.Id);}
                            }
                            chart??=await store.GetHistoryImageAsync(signal.Id,token);
                            var id=await sender.PublishAsync(signal,chart,token,currentImage);
                            var imageSent=chart is not null && id is not null;
                            if(id is null && chart is not null && sender.RetryAfterUtc is null)id=await sender.PublishAsync(signal,token);
                            if(id is null)
                            {
                                await SaveRetryAsync(ledger,destination.Key,sender,token);
                                continue; // Retry this destination; do not drop outcomes.
                            }
                            ledger.Publications[rootKey]=id.Value;
                            await SaveAsync(store.NotificationLedgerPath,ledger,token);
                            await store.SavePublicDeliveryAsync(signal.Id,id.Value,imageSent,token);
                        }
                        if(ledger.Publications.TryGetValue(rootKey,out var root))signal.PublicTelegramMessageId=root;
                        else if(publicationAt is not null || !notifier.IsDedicatedSignal(signal))continue;
                        foreach(var e in Events(signal,notifier.IsDedicatedSignal(signal)).OrderBy(e=>e.At))
                        {
                            var key=$"{rootKey}:{e.Kind}";
                            // Legacy keys predate per-destination delivery. Respect them on migration.
                            if(e.At<ledger.SinceUtc || ledger.Sent.Contains(key) || ledger.Sent.Contains($"{signal.Id}:{e.Kind}"))continue;
                            var id=await SendEventAsync(sender,signal,e.Kind,token);
                            if(id is null)
                            {
                                await SaveRetryAsync(ledger,destination.Key,sender,token);
                                break; // Preserve event order only for this signal/destination.
                            }
                            ledger.Sent.Add(key);
                            await SaveAsync(store.NotificationLedgerPath,ledger,token);
                        }
                    }
                    catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                    catch(Exception ex){logger.LogWarning(ex,"Public delivery failed for {SignalId} at {Destination}",signal.Id,destination.Key);}
                }
            }
        }
        finally{cycleGate.Release();}
    }

    private async Task<int?> SendEventAsync(PublicSignalNotifier sender,ServerSignal signal,string kind,CancellationToken token)
    {
        if(kind is "Target" or "StopLoss")
        {
            var image=await store.GetHistoryImageAsync(signal.Id,token);
            var id=kind=="Target"?await sender.TargetReachedAsync(signal,image,token):await sender.StopLossReachedAsync(signal,image,token);
            return id??(sender.RetryAfterUtc is not null?null:kind=="Target"?await sender.TargetReachedAsync(signal,token):await sender.StopLossReachedAsync(signal,token));
        }
        if(kind=="Expired")
        {
            var image=signal.ExpireReason=="TargetAfterActivation" && signal.ExpiryPriceTrail.Count>0
                ? SignalChartRenderer.RenderExpiryEvidence(signal):null;
            var id=await sender.ExpiredAsync(signal,image,token);
            return id??(sender.RetryAfterUtc is not null?null:await sender.ExpiredAsync(signal,token));
        }
        if(kind=="RiskFree")
        {
            byte[]? image=null;var current=false;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try{image=(await entryReview.BuildCurrentAsync(signal,timeout.Token)).Image;current=true;}
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch{image=await store.GetHistoryImageAsync(signal.Id,token);}
            var id=await sender.RiskFreeClosedAsync(signal,image,current,token);
            return id??(image is null || sender.RetryAfterUtc is not null?null:await sender.RiskFreeClosedAsync(signal,token));
        }
        return kind switch
        {
            "Opened"=>await sender.OpenedAsync(signal,token),
            "RiskFreeReached"=>await sender.RiskFreeReachedAsync(signal,token),
            "ManualClosed"=>await sender.ManuallyClosedAsync(signal,token),
            _=>await sender.CancelledAsync(signal,token)
        };
    }

    private async Task SaveRetryAsync(Ledger ledger,string key,PublicSignalNotifier sender,CancellationToken token)
    {
        if(sender.RetryAfterUtc is not { } at)return;
        ledger.RetryAfterUtc[key]=at;
        await SaveAsync(store.NotificationLedgerPath,ledger,token);
    }

    private static async Task SaveAsync(string path,Ledger ledger,CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path+".tmp",JsonSerializer.Serialize(ledger),token);
        File.Move(path+".tmp",path,true);
    }
}

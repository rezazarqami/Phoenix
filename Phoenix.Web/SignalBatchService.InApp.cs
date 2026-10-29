using System.Text.Json;
namespace Phoenix.Web;

public sealed partial class SignalBatchService
{
    private readonly SemaphoreSlim reviewGate = new(1, 1);
    private static readonly JsonSerializerOptions ReviewJson = new(JsonSerializerDefaults.Web);
    private static string PendingPath => Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")
        ?? Path.Combine(AppContext.BaseDirectory,"data","server-signals.json"))!,"in-app-proposals.json");
    public sealed record PendingProposal(string Key, string Owner, SignalCandidate Candidate, string Interval,
        bool LineMode, ProfessionalSignalAnalysis Analysis, string Caption, byte[] Image, decimal PositionSizeUsdt,
        DateTime CreatedAt, string State = "Ready", string? Error = null);
    private async Task<List<PendingProposal>> ReadProposalsAsync(CancellationToken ct) => File.Exists(PendingPath)
        ? JsonSerializer.Deserialize<List<PendingProposal>>(await File.ReadAllTextAsync(PendingPath,ct),ReviewJson)
            ?? throw new InvalidDataException("صف پیشنهادها قابل خواندن نیست.") : [];
    private async Task WriteProposalsAsync(List<PendingProposal> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
        var temp=PendingPath+".tmp";
        await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(items,ReviewJson),ct);
        File.Move(temp,PendingPath,true);
    }
    public async Task<IReadOnlyList<PendingProposal>> PendingAsync(string owner,CancellationToken ct)
    {
        await reviewGate.WaitAsync(ct);
        try{return (await ReadProposalsAsync(ct)).Where(x=>x.Owner.Equals(owner,StringComparison.OrdinalIgnoreCase)).ToArray();}
        finally{reviewGate.Release();}
    }
    private async Task<int> PendingCountAsync(CancellationToken ct)
    {
        await reviewGate.WaitAsync(ct);
        try{return (await ReadProposalsAsync(ct)).Count;}
        finally{reviewGate.Release();}
    }
    internal async Task WaitForCapacityAsync(int target,DateTimeOffset? endsAt,CancellationToken ct)
    {
        while(true){ct.ThrowIfCancellationRequested();if(endsAt is {} end && DateTimeOffset.UtcNow>=end)return;
            var count=await PendingCountAsync(ct);
            if(count<10 && (endsAt.HasValue || count+Status.Approved+Status.Observed<target))return;
            if(!endsAt.HasValue && Status.Approved+Status.Observed>=target)return;
            Update(s=>s with{Message="صف تأیید آماده است؛ برای ادامهٔ تحلیل یک پیشنهاد را بررسی کنید."});
            await Task.Delay(300,ct);
        }
    }
    internal async Task AddProposalAsync(PendingProposal item,CancellationToken ct)
    {
        await reviewGate.WaitAsync(ct);
        try{var items=await ReadProposalsAsync(ct);if(items.Count>=10)throw new InvalidOperationException("صف تأیید پر است.");
            items.Add(item);await WriteProposalsAsync(items,ct);}
        finally{reviewGate.Release();}
    }
    public async Task<string?> DecideInAppAsync(string owner,string key,string action,string? reason,CancellationToken ct)
    {
        if(action is not ("register" or "observe" or "reject"))return "انتخاب معتبر نیست.";
        if(reason?.Length>1500)return "دلیل رد حداکثر ۱۵۰۰ نویسه است.";
        PendingProposal item;
        await reviewGate.WaitAsync(ct);
        try
        {
            var items=await ReadProposalsAsync(ct);
            var found=items.SingleOrDefault(x=>x.Key==key && x.Owner.Equals(owner,StringComparison.OrdinalIgnoreCase));
            if(found is null)return "این پیشنهاد قبلاً بررسی شده است.";item=found;
            if(item.State!="Ready")return "نتیجهٔ ارسال قبلی نیازمند بررسی است؛ برای جلوگیری از معاملهٔ تکراری دوباره ارسال نمی‌شود.";

            // Commit the claim before any trading side effect. A crash cannot replay a submission.
            items[items.IndexOf(item)]=item with{State="Processing"};
            await WriteProposalsAsync(items,ct);
        }finally{reviewGate.Release();}
        var selected=item.Candidate;
        try
        {
                if(action=="reject"){
                    if(!await reviews.DecideAsync(key,false,reason,ct))throw new InvalidOperationException("ذخیرهٔ رد ناموفق بود.");
                    Update(s=>s with{Rejected=s.Rejected+1});
                }
                else if(action=="observe"){
                    var observed=await shadow.AddAsync(selected,item.Interval,item.LineMode,item.Image,item.Analysis,owner,ct);
                    await reviews.ObserveAsync(key,ct);await reviews.LinkSignalAsync(key,observed.Id,ct);
                    Update(s=>s with{Observed=s.Observed+1});
                }
                else{
                    var analysis=item.Analysis;
                    var outcome=await submission.QueueAsync(new SignalRequest(selected.Symbol,selected.Direction,selected.Ceiling,selected.Floor,item.PositionSizeUsdt),ct,
                        new SignalEvidence(item.Interval,item.LineMode?"Line":"Candles",item.Image,analysis.Features,
                            string.Join(" | ",analysis.Strengths.Concat(analysis.Risks)),analysis.MarketRegime,
                            SignalProposalEvidence.Capture(selected,item.Interval,analysis)),owner,notifyTelegram:false);
                    if(outcome.Signal is null)throw new InvalidOperationException(outcome.Error??"ثبت سیگنال ناموفق بود.");
                    await reviews.DecideAsync(key,true,ct);await reviews.LinkSignalAsync(key,outcome.Signal.Id,ct);
                    Update(s=>s with{Approved=s.Approved+1});
                }
                await reviewGate.WaitAsync(CancellationToken.None);try{var items=await ReadProposalsAsync(CancellationToken.None);items.RemoveAll(x=>x.Key==key);await WriteProposalsAsync(items,CancellationToken.None);}finally{reviewGate.Release();}return null;
            }
        catch(Exception e){
            await reviewGate.WaitAsync(CancellationToken.None);try{var items=await ReadProposalsAsync(CancellationToken.None);var index=items.FindIndex(x=>x.Key==key);if(index>=0)items[index]=item with{State="NeedsReview",Error="عملیات کامل نشد؛ وضعیت سفارش را پیش از تلاش دوباره بررسی کنید."};await WriteProposalsAsync(items,CancellationToken.None);}finally{reviewGate.Release();}
            logger.LogError(e,"In-app proposal decision failed {Key}",key);return "عملیات کامل نشد؛ وضعیت در صف نمایش داده می‌شود.";
        }
    }
}
public sealed record InAppDecisionRequest(string Action,string? Reason);

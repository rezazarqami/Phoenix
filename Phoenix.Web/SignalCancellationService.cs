using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed record SignalCancellationResult(bool Cancelled, string Message);

public sealed class SignalCancellationService(ServerOrderStore store, BybitDemoClient bybit)
{
    public async Task<IReadOnlyList<SelectedCancellationResult>> CancelSelectedAsync(Guid[] ids,CancellationToken token=default)
    {
        if(ids.Length is <1 or >100)throw new ArgumentException("بین ۱ تا ۱۰۰ سیگنال انتخاب کن.");
        var results=new List<SelectedCancellationResult>();
        foreach(var id in ids.Distinct())
        {
            // CancelAsync takes the execution gate and checks current exchange/store state for each ID.
            var current=(await store.GetAllAsync(token)).SingleOrDefault(s=>s.Id==id);
            if(current is null||current.Status is not ("Pending" or "Submitted")||current.FilledAtUtc is not null||current.CompletedAtUtc is not null)
            {results.Add(new(id,false,"دیگر در انتظار ورود نیست؛ لغو نشد."));continue;}
            try{var result=await CancelAsync(id,token);results.Add(new(id,result.Cancelled,result.Message));}
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch{results.Add(new(id,false,"لغو تأیید نشد؛ وضعیت صرافی را بررسی کن."));}
        }
        return results;
    }

    public async Task<SignalCancellationResult> CancelAsync(Guid id, CancellationToken token = default)
    {
        await store.ExecutionGate.WaitAsync(token);
        try
        {
            var signal = (await store.GetAllAsync(token)).SingleOrDefault(x => x.Id == id);
            if (signal is null) return new(false, "سیگنال پیدا نشد.");
            if (signal.Status == "Submitting")
                return new(false, "سفارش در حال ارسال است؛ چند ثانیه بعد دوباره لغو را بزنید.");
            if (signal.Status is "Filled" or "Closing" || signal.FilledAtUtc is not null)
                return new(false, "معامله وارد پوزیشن شده است؛ از گزینه بستن پوزیشن استفاده کنید.");
            if (signal.CompletedAtUtc is not null || signal.Status is not ("Pending" or "Submitted" or "Error"))
                return new(false, "این سیگنال قبلاً بسته یا لغو شده است.");
            if (signal.Status == "Submitted" || !string.IsNullOrWhiteSpace(signal.BybitOrderId))
            {
                if (string.IsNullOrWhiteSpace(signal.BybitOrderId))
                    return new(false, "شناسه سفارش صرافی موجود نیست؛ وضعیت سفارش را بررسی کنید.");
                await bybit.CancelOrderAsync(signal.Symbol, signal.BybitOrderId, token);
                // Bybit cancellation acknowledgement is asynchronous. Do not report a
                // successful cancellation until the exchange confirms no execution.
                var confirmed = false;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var current = await bybit.GetOrderStatusAsync(signal.BybitOrderId, token);
                    if (current?.Status == "Cancelled" && current.ExecutedQuantity == 0)
                    { confirmed = true; break; }
                    if (current?.Status is "Filled" or "PartiallyFilled" || current?.ExecutedQuantity > 0)
                        return new(false, "سفارش در صرافی اجرا شده است؛ وضعیت پوزیشن را بررسی کنید.");
                    if (attempt < 3) await Task.Delay(250, token);
                }
                if (!confirmed) return new(false, "درخواست لغو به صرافی ارسال شد؛ لغو نهایی هنوز تأیید نشده است. دوباره وضعیت را بررسی کنید.");
            }
            return await store.CancelEntryReviewAsync(id, token)
                ? new(true, "سیگنال لغو شد 🚫") : new(false, "این سیگنال دیگر قابل لغو نیست.");
        }
        finally { store.ExecutionGate.Release(); }
    }
}

public sealed record SelectedCancellationResult(Guid Id,bool Cancelled,string Message);
public sealed record CancelSelectedRequest(Guid[] Ids);

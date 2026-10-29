using System.Collections.Concurrent;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed record ClosePreview(Guid Id, DateTime ExpiresUtc, string Account,
    IReadOnlyList<BybitOpenPosition> Positions, bool Single=false,string? Owner=null);
public sealed record CloseItem(string Symbol, string Side, bool Submitted, string? Error);

public sealed class BulkPositionService(ServerOrderStore store, BybitDemoClient client,
    BybitDemoOptions options, ILogger<BulkPositionService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, ClosePreview> _previews = new();
    public async Task<ClosePreview> PreviewAsync(CancellationToken token,string? symbol=null,string? side=null,int? positionIndex=null,string? owner=null)
    {
        foreach (var pair in _previews.Where(x => x.Value.ExpiresUtc < DateTime.UtcNow))
            _previews.TryRemove(pair.Key, out _);
        var positions=await client.GetOpenPositionsAsync(token);
        if(symbol is not null){if(side is not ("Buy" or "Sell")||positionIndex is null)throw new ArgumentException("پوزیشن معتبر نیست.");positions=positions.Where(p=>p.Symbol==symbol&&p.Side==side&&p.PositionIndex==positionIndex).ToArray();}
        var preview = new ClosePreview(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(2),
            options.EnvironmentName, positions,symbol is not null,owner);
        _previews[preview.Id] = preview;
        return preview;
    }

    public async Task<IReadOnlyList<CloseItem>> CloseAsync(Guid previewId, CancellationToken token)
    {
        if (!_previews.TryRemove(previewId, out var preview) || preview.ExpiresUtc < DateTime.UtcNow)
            throw new InvalidOperationException("تأیید منقضی یا قبلاً استفاده شده است؛ فهرست را دوباره دریافت کنید.");
        await store.ExecutionGate.WaitAsync(token);
        try
        {
            await store.SetEntriesPausedAsync(true, token);
            var signals = await store.GetAllAsync(token);
            if (signals.Any(x => (x.Status is "Submitting" or "Submitted") && (!preview.Single || preview.Positions.Any(p=>p.Symbol==x.Symbol&&x.Direction==(p.Side=="Buy"?"Long":"Short")))))
                throw new InvalidOperationException("ورودها متوقف شد. سفارش در حال ارسال یا منتظر اجرا وجود دارد؛ ابتدا وضعیت آن را تعیین کنید و دوباره تأیید کنید.");
            var current = await client.GetOpenPositionsAsync(token);
            var result = new List<CloseItem>();
            foreach (var approved in preview.Positions)
            {
                var p = current.SingleOrDefault(x => x.Symbol == approved.Symbol &&
                    x.Side == approved.Side && x.PositionIndex == approved.PositionIndex);
                if (p is null) continue;
                if (p.Size > approved.Size)
                {
                    result.Add(new(p.Symbol, p.Side, false, "حجم از زمان تأیید افزایش یافته؛ تأیید مجدد لازم است."));
                    continue;
                }
                var linked = signals.Where(x => x.CompletedAtUtc is null &&
                    x.Status is "Filled" or "Closing" && x.Symbol == p.Symbol &&
                    x.Direction == (p.Side == "Buy" ? "Long" : "Short")).ToArray();
                if(linked.Length==0){var audit=new ServerSignal{Id=Guid.NewGuid(),CreatedAtUtc=DateTime.UtcNow,Symbol=p.Symbol,Direction=p.Side=="Buy"?"Long":"Short",Status="Closing",EntryPrice=p.AveragePrice??0,FilledAtUtc=DateTime.UtcNow,Quantity=p.Size,PositionSizeUsdt=p.InitialMargin??0,Leverage=p.Leverage,RequestedByUsername=preview.Owner,Origin="ExternalPosition"};await store.AddAsync(audit,token);linked=[audit];}
                // Persist intent before the exchange call; a timeout must never be
                // reported as a confirmed close or become a target/stop result.
                foreach (var s in linked) { s.Status = "Closing"; await store.UpdateAsync(s, token); }
                try
                {
                    await client.ClosePositionAsync(p, token);
                    result.Add(new(p.Symbol, p.Side, true, null));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Bulk close uncertain/failed for {Symbol}", p.Symbol);
                    result.Add(new(p.Symbol, p.Side, false, "ارسال ناموفق یا وضعیت نامشخص؛ وضعیت صرافی را بررسی و در صورت نیاز دوباره اقدام کنید."));
                }
            }
            return result;
        }
        finally { store.ExecutionGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await store.ExecutionGate.WaitAsync(token);
                try
                {
                    var closing = (await store.GetAllAsync(token)).Where(x => x.Status == "Closing").ToArray();
                    if (closing.Length > 0)
                    {
                        var positions = await client.GetOpenPositionsAsync(token);
                        foreach (var s in closing)
                        {
                            if (positions.Any(x => x.Symbol == s.Symbol &&
                                x.Side == (s.Direction == "Long" ? "Buy" : "Sell"))) continue;
                            foreach (var protectionOrderId in StagedRiskProtection.OrderIds(s))
                            {
                                if (string.IsNullOrWhiteSpace(protectionOrderId)) continue;
                                var protection = await client.GetOrderStatusAsync(protectionOrderId, token);
                                if (protection is not null && protection.Status is not
                                    ("Filled" or "Cancelled" or "Deactivated" or "Rejected"))
                                    await client.CancelOrderAsync(s.Symbol, protectionOrderId, token);
                            }
                            s.Status = "Completed";
                            s.Outcome = "ManualClosed";
                            s.CompletedAtUtc = DateTime.UtcNow;
                            await store.UpdateAsync(s, token);
                            await WalletNotification.CaptureAsync(client,store,s,token);
                        }
                    }
                }
                finally { store.ExecutionGate.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Bulk close reconciliation failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }
}

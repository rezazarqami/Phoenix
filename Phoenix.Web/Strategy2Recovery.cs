using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed record Strategy2RecoveryResult(int RecoveredRecords, int CancelledPending, bool EntriesPaused);

// Never replay archived entries, submit an exchange order, or infer TP/SL from missing history.
public sealed class Strategy2Recovery(ServerOrderStore store, BybitDemoClient client, ServerOrderStore? mainStore = null)
{
    public async Task<Strategy2RecoveryResult> ReconcileAsync(CancellationToken token)
    {
        await store.ExecutionGate.WaitAsync(token);
        try
        {
            var snapshot = await store.GetAllAsync(token);
            var recovered = snapshot.Where(s => s.Status == "RecoveryRequired").ToArray();
            if (recovered.Length == 0) return new(0, 0, false);
            // Require successful, account-wide checks. Null historical order results are NOT evidence.
            if ((await client.GetOpenPositionsAsync(token)).Count != 0 ||
                (await client.GetOpenLinearOrdersAsync(token)).Count != 0)
                return new(0, 0, true);
            // Recheck positions after listing orders, in case a previously live order just filled.
            if ((await client.GetOpenPositionsAsync(token)).Count != 0) return new(0, 0, true);
            var retained=new Dictionary<Guid,decimal>();
            if(mainStore is not null)
            {
                var main=await mainStore.GetAllAsync(token);
                foreach(var pending in snapshot.Where(s=>(s.Status=="Pending" ||
                    s.Status=="RecoveryRequired" && s.RecoveryOriginalStatus=="Pending") &&
                    s.CompletedAtUtc is null && s.SubmittedAtUtc is null && s.EntryTriggeredAtUtc is null &&
                    s.BybitOrderId is null && s.FilledAtUtc is null))
                {
                    var counterpart=main.FirstOrDefault(s=>s.Status=="Pending" && s.CompletedAtUtc is null && s.EntryTriggeredAtUtc is null &&
                        s.Symbol==pending.Symbol && s.Direction==pending.Direction && s.Timeframe==pending.Timeframe &&
                        s.EntryPrice==pending.EntryPrice && s.TakeProfit==pending.TakeProfit && s.StopLoss==pending.StopLoss &&
                        s.RequestedByUsername==pending.RequestedByUsername);
                    if(counterpart is null)continue;
                    // Never replay an entry already passed during the pause. Retain only a
                    // still-waiting main counterpart with fresh price on the waiting side.
                    var price=(await client.GetLastPriceAsync(pending.Symbol,token)).LastPrice;
                    var valid=pending.Direction=="Long"? price>pending.EntryPrice && price<pending.Ceiling:
                        pending.Direction=="Short" && price<pending.EntryPrice && price>pending.Floor;
                    var current=await mainStore.GetAllAsync(token);
                    if(valid && price>0 && current.Any(s=>s.Id==counterpart.Id && s.Status=="Pending" &&
                        s.EntryTriggeredAtUtc is null && s.CompletedAtUtc is null))retained[pending.Id]=price;
                }
            }
            return await store.ResolveFlatStrategy2RecoveryAsync(token,retained);
        }
        finally { store.ExecutionGate.Release(); }
    }
}

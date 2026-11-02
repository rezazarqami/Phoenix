using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed record Strategy2RecoveryResult(int RecoveredRecords, int CancelledPending, bool EntriesPaused);

// Never replay archived entries, submit an exchange order, or infer TP/SL from missing history.
public sealed class Strategy2Recovery(ServerOrderStore store, BybitDemoClient client)
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
            return await store.ResolveFlatStrategy2RecoveryAsync(token);
        }
        finally { store.ExecutionGate.Release(); }
    }
}

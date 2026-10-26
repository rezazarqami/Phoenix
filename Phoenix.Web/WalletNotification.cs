using System.Globalization;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public static class WalletNotification
{
    // Capture once after terminal persistence; never backfill an old result with today's balance.
    public static async Task CaptureAsync(BybitDemoClient client, ServerOrderStore store, ServerSignal signal, CancellationToken token)
    {
        if (signal.ResultWalletAtUtc is not null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var balance = await client.GetUsdtWalletBalanceAsync(timeout.Token);
            var at = DateTime.UtcNow;
            await store.SaveResultWalletAsync(signal.Id, balance, at, token);
            signal.ResultWalletBalanceUsdt = balance; signal.ResultWalletAtUtc = at;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* The terminal result remains available even if the exchange is unavailable. */ }
    }

    public static async Task<string> ReadAsync(BybitDemoClient client, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var balance = await client.GetUsdtWalletBalanceAsync(timeout.Token);
            return $"\n💰 موجودی کیف پول USDT در زمان اعلان: {balance.ToString("0.########", CultureInfo.InvariantCulture)} USDT\n(ممکن است تسویه صرافی هنوز در حال انجام باشد.)";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return "\n💰 موجودی کیف پول: دریافت نشد؛ نتیجه سیگنال محفوظ است."; }
    }
}

using System.Globalization;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public static class WalletNotification
{
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

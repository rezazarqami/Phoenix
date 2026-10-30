using Phoenix.Engine.Exchanges.Bybit;
namespace Phoenix.Web;

// Independent, read-only checks; no trades, Telegram or queue-file locks.
public sealed class BybitConnectionWorker(BybitDemoClient client, BybitDemoOptions options,
    ServerState state, ILogger<BybitConnectionWorker> logger) : BackgroundService
{
    private DateTime nextAccountCheck=DateTime.MinValue;
    internal async Task CheckNowAsync(CancellationToken token, DateTime? scheduledAt = null)
    {
        async Task Probe(bool account)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                if(account)await client.CheckAccountConnectionAsync(timeout.Token);
                else await client.CheckPublicConnectionAsync(timeout.Token);
                if(account)state.RecordAccount(true,null,DateTime.UtcNow);else state.RecordPublic(true,null,DateTime.UtcNow);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                var message=ex is OperationCanceledException?"مهلت پاسخ بای‌بیت پایان یافت.":ex.Message;
                if(account)state.RecordAccount(false,message,DateTime.UtcNow);else state.RecordPublic(false,message,DateTime.UtcNow);
                logger.LogWarning("Bybit {Probe} connectivity check failed: {Error}",account?"account":"public",message);
            }
        }
        var now=scheduledAt??DateTime.UtcNow;
        var publicCheck=Probe(false);
        if(options.HasCredentials && now>=nextAccountCheck)
        {
            nextAccountCheck=now.AddSeconds(45);
            await Task.WhenAll(publicCheck,Probe(true));
        }
        else await publicCheck;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try { await CheckNowAsync(stoppingToken); await Task.Delay(TimeSpan.FromSeconds(15),stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
        }
    }
}

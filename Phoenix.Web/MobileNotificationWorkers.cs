using System.Collections.Concurrent;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class MobilePushDeliveryWorker(MobilePushService push, ILogger<MobilePushDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await push.DeliverPendingAsync(DateTimeOffset.UtcNow, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Mobile push delivery failed ({FailureType})", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }
}

public sealed class Strategy2JournalWorker(Strategy2Runtime strategy, DedicatedTelegramOptions dedicated,
    ILogger<Strategy2JournalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (PhoenixSessionAuth.CredentialsConfigured(out var owner, out _))
                    await strategy.Journal(dedicated).CaptureAsync(owner, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Strategy two journal capture failed ({FailureType})", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }
}

// Read-only confirmation for the notification path. The trading worker still owns all order transitions.
public sealed class MobileEntryConfirmationWorker(ServerOrderStore store, BybitDemoClient client,
    BybitDemoOptions options, DedicatedTelegramOptions dedicated, MobileSignalFeed feed,
    MobilePushService push, ILogger<MobileEntryConfirmationWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<(Guid Signal, string Order), byte> confirmed = new();

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!options.HasCredentials) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (PhoenixSessionAuth.CredentialsConfigured(out var owner, out _))
                    await ConfirmAsync(owner, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Mobile entry confirmation cycle failed ({FailureType})", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    internal async Task ConfirmAsync(string owner, CancellationToken token)
    {
        var submitted = (await store.GetAllAsync(token))
            .Where(signal => signal.Status == "Submitted" && signal.CompletedAtUtc is null &&
                signal.FilledAtUtc is null && signal.Origin != "ExternalPosition" &&
                !string.IsNullOrWhiteSpace(signal.BybitOrderId) && MobileSignalWorker.CanNotify(signal, owner, dedicated))
            .ToArray();
        var active = submitted.Select(signal => (signal.Id, signal.BybitOrderId!)).ToHashSet();
        foreach (var key in confirmed.Keys)
            if (!active.Contains(key)) confirmed.TryRemove(key, out _);

        await Parallel.ForEachAsync(submitted, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (signal, cancellation) =>
            {
                var key = (signal.Id, signal.BybitOrderId!);
                if (confirmed.ContainsKey(key)) return;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var status = await client.GetOrderStatusAsync(key.Item2, timeout.Token);
                    if (status is not { Status: "Filled", ExecutedQuantity: > 0 } ||
                        status.OrderId != key.Item2 || !string.Equals(status.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase))
                        return;
                    var now = DateTimeOffset.UtcNow;
                    signal.FilledAtUtc = status.UpdatedAtUtc ?? now.UtcDateTime;
                    if (signal.FilledAtUtc.Value > now.UtcDateTime) return;
                    var opened = MobileSignalWorker.Events(signal, owner, dedicated).SingleOrDefault(item => item.Kind == "Opened");
                    if (opened is null) return;
                    await feed.RecordConfirmedEntryAsync(owner, signal, opened, cancellation);
                    if (push.Configured) await push.QueueAsync(owner, [opened], now, cancellation);
                    confirmed.TryAdd(key, 0);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning("Mobile entry confirmation failed for {SignalId} ({FailureType})", signal.Id, ex.GetType().Name); }
            });
    }
}

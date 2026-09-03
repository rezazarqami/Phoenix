using System.Text.Json;

namespace Phoenix.Web;

// Delivery state is separate from trading snapshots so notification retries cannot
// overwrite a concurrently claimed entry or a terminal trading result.
public sealed class PublicSignalNotificationWorker(
    ServerOrderStore store, PublicSignalNotifier notifier,
    ILogger<PublicSignalNotificationWorker> logger) : BackgroundService
{
    public sealed class Ledger
    {
        public DateTime SinceUtc { get; set; } = DateTime.UtcNow;
        public HashSet<string> Sent { get; set; } = [];
    }

    public static IEnumerable<(string Kind, DateTime At)> Events(ServerSignal s) => Events(s, false);

    public static IEnumerable<(string Kind, DateTime At)> Events(ServerSignal s, bool resultsOnly)
    {
        if (s.PublicTelegramMessageId is not > 0) yield break;
        if (!resultsOnly && s.FilledAtUtc is { } opened) yield return ("Opened", opened);
        if (s.RiskFreeReachedAtUtc is { } activated) yield return ("RiskFreeReached", activated);
        if (s.CompletedAtUtc is not { } ended) yield break;
        if (s.Outcome is "Target" or "StopLoss" or "RiskFree") yield return (s.Outcome, ended);
        if (s.Outcome == "Expired" && s.ExpireReason == "TargetAfterActivation")
            yield return ("Expired", ended);
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var path = store.NotificationLedgerPath;
        Ledger? ledger = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (ledger is null)
                {
                    var loaded = File.Exists(path)
                        ? JsonSerializer.Deserialize<Ledger>(await File.ReadAllTextAsync(path, token))
                            ?? throw new InvalidDataException("Invalid public notification ledger.")
                        : new Ledger();
                    await SaveAsync(path, loaded, token);
                    ledger = loaded;
                }
                foreach (var s in await store.GetAllAsync(token))
                foreach (var e in Events(s, notifier.IsDedicatedSignal(s)).OrderBy(x => x.At))
                {
                    var key = $"{s.Id}:{e.Kind}";
                    if (e.At < ledger.SinceUtc || ledger.Sent.Contains(key)) continue;
                    var messageId = e.Kind switch
                    {
                        "Opened" => await notifier.OpenedAsync(s, token),
                        "RiskFreeReached" => await notifier.RiskFreeReachedAsync(s, token),
                        "Target" => await notifier.TargetReachedAsync(s, token),
                        "StopLoss" => await notifier.StopLossReachedAsync(s, token),
                        "RiskFree" => await notifier.RiskFreeClosedAsync(s, token),
                        _ => await notifier.ExpiredAsync(s, token)
                    };
                    if (messageId is null) break; // Retry, preserving per-signal event order.
                    ledger.Sent.Add(key);
                    await SaveAsync(path, ledger, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Public signal delivery cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    private static async Task SaveAsync(string path, Ledger ledger, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(ledger), token);
        File.Move(path + ".tmp", path, true);
    }
}

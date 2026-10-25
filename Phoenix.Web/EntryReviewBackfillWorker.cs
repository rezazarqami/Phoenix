namespace Phoenix.Web;

public sealed class EntryReviewBackfillWorker(
    ServerOrderStore store,
    EntrySignalReviewService reviews,
    ILogger<EntryReviewBackfillWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), token);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var missing = (await store.GetAllAsync(token))
                    .Where(x => x.EntryReviewSentAtUtc is null && x.Status is "Submitting" or "Submitted")
                    .OrderByDescending(x => x.SubmittedAtUtc ?? x.CreatedAtUtc).Take(5).ToArray();
                foreach (var signal in missing)
                {
                    await reviews.RefreshAndNotifyAsync(signal, token);
                    if (signal.EntryReviewSentAtUtc.HasValue) await store.UpdateAsync(signal, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Entry review backfill cycle failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), token);
        }
    }
}

namespace Phoenix.Web;

public sealed class PublicSignalImageBackfillWorker(
    ServerOrderStore store,
    EntrySignalReviewService reviews,
    PublicSignalNotifier telegram,
    ILogger<PublicSignalImageBackfillWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), token);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var missing = (await store.GetAllAsync(token)).Where(x =>
                        x.PublicTelegramMessageId is > 0 && x.PublicReviewImageSentAtUtc is null &&
                        x.CompletedAtUtc is null)
                    .OrderByDescending(x => x.ExpireAdjustedAtUtc ?? x.CreatedAtUtc).Take(5).ToArray();
                foreach (var signal in missing)
                {
                    var review = await reviews.BuildCurrentAsync(signal, token);
                    if (await telegram.SendCurrentReviewAsync(signal, review.Image, token) is null) continue;
                    signal.PublicReviewImageSentAtUtc = DateTime.UtcNow;
                    await store.UpdateAsync(signal, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Public signal image backfill failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), token);
        }
    }
}

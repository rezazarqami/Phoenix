namespace Phoenix.Web;

public sealed class PublicSignalCommandWorker(
    PublicSignalNotifier telegram,
    TelegramAccessStore access,
    ServerOrderStore store,
    ILogger<PublicSignalCommandWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        long offset = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var commands = await telegram.GetCommandsAsync(offset, token);
                foreach (var command in commands)
                {
                    offset = Math.Max(offset, command.UpdateId + 1);
                    if (!command.Command.StartsWith("public:cancel:", StringComparison.Ordinal) ||
                        !Guid.TryParseExact(command.Command["public:cancel:".Length..], "N", out var id)) continue;
                    var users = await access.GetAllAsync(token);
                    var authorized = users.Any(x => x.Enabled && x.UserId == command.UserId);
                    var answer = !authorized
                        ? "شما اجازه لغو این سیگنال را ندارید."
                        : await store.CancelEntryReviewAsync(id, token)
                            ? "سیگنال لغو شد 🚫"
                            : "این سیگنال دیگر قابل لغو نیست.";
                    if (!string.IsNullOrWhiteSpace(command.CallbackId))
                        await telegram.AnswerCallbackAsync(command.CallbackId, answer, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Public signal callback polling failed");
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
        }
    }
}

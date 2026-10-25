namespace Phoenix.Web;

public sealed class PublicSignalCommandWorker(
    PublicSignalNotifier telegram,
    TelegramAccessStore access,
    SignalCancellationService cancellation,
    TelegramOptions privateOptions,
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
                    await HandleCommandAsync(command, id, token);
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
    public async Task HandleCommandAsync(TelegramCommand command, Guid id, CancellationToken token)
    {
        string answer;
        try
        {
            var users = await access.GetAllAsync(token);
            var registered = users.SingleOrDefault(x => x.UserId == command.UserId);
            var authorized = registered is not null ? registered.Enabled :
                (long.TryParse(privateOptions.ChatId, out var ownerId) && ownerId > 0 && ownerId == command.UserId) ||
                await telegram.IsChannelAdministratorAsync(command, token);
            answer = authorized ? (await cancellation.CancelAsync(id, token)).Message
                : "شما اجازه لغو این سیگنال را ندارید؛ فقط مدیر کانال یا کاربر مجاز می‌تواند لغو کند.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Public cancellation failed for {SignalId}", id);
            answer = "لغو انجام نشد؛ بررسی مجوز یا ارتباط با سرور ناموفق بود. دوباره تلاش کنید.";
        }
        if (!string.IsNullOrWhiteSpace(command.CallbackId))
            await telegram.AnswerCallbackAsync(command.CallbackId, answer, token);
    }

}

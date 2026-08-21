using System.Globalization;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class TelegramCommandWorker(
    TelegramNotifier telegram,
    ServerOrderStore store,
    ServerState state,
    BybitDemoOptions options,
    SignalBatchService batches,
    ILogger<TelegramCommandWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!telegram.IsConfigured) return;
        try { await telegram.ConfigureMenuAsync(stoppingToken); }
        catch (Exception exception) { logger.LogWarning(exception, "Telegram menu configuration failed"); }

        long offset;
        try { offset = await telegram.GetInitialUpdateOffsetAsync(stoppingToken); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Telegram initial update offset failed");
            offset = 0;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var commands = await telegram.GetCommandsAsync(offset, stoppingToken);
                foreach (var command in commands)
                {
                    offset = Math.Max(offset, command.UpdateId + 1);
                    if (!telegram.IsAuthorizedChat(command.ChatId)) continue;
                    if (command.Command.StartsWith("batch:", StringComparison.Ordinal))
                    {
                        await batches.HandleCallbackAsync(command.Command, command.CallbackId, stoppingToken);
                        continue;
                    }
                    await telegram.SendCommandReplyAsync(command.ChatId,
                        await BuildReplyAsync(command.Command, stoppingToken), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Telegram command polling failed");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task<string> BuildReplyAsync(string command, CancellationToken token)
    {
        var signals = await store.GetAllAsync(token);
        return command switch
        {
            "/start" or "/help" =>
                "🔥 به دستیار خصوصی Phoenix خوش آمدید.\n\n/status وضعیت موتور\n/active سیگنال‌های فعال\n/results پنج نتیجه آخر\n/help راهنما",
            "/status" => BuildStatus(signals),
            "/active" => BuildActive(signals),
            "/results" => await BuildResultsAsync(token),
            _ => "دستور شناخته نشد. برای دیدن گزینه‌ها /help را بزنید."
        };
    }

    private string BuildStatus(IReadOnlyList<ServerSignal> signals)
    {
        var active = signals.Count(x => x.Status is "Pending" or "Submitting" or "Submitted" or "Filled");
        return $"📡 وضعیت Phoenix\nاتصال عمومی Bybit: {(state.PublicApiConnected ? "متصل ✅" : "قطع ❌")}\n" +
               $"حساب {options.EnvironmentName}: {(state.DemoAuthenticated ? "متصل ✅" : "قطع ❌")}\n" +
               $"موتور سفارش: {(DemoOrderWorker.IsTradingEnabled(options) ? "فعال ✅" : "خاموش ⏸")}\n" +
               $"سیگنال فعال: {active}\nآخرین به‌روزرسانی: {FormatTime(state.LastUpdatedUtc)}";
    }

    private static string BuildActive(IReadOnlyList<ServerSignal> signals)
    {
        var active = signals.Where(x => x.Status is "Pending" or "Submitting" or "Submitted" or "Filled")
            .OrderByDescending(x => x.CreatedAtUtc).Take(15).ToArray();
        if (active.Length == 0) return "📭 در حال حاضر سیگنال فعالی وجود ندارد.";
        return "📋 سیگنال‌های فعال\n\n" + string.Join("\n", active.Select(x =>
            $"• {x.Symbol} | {x.Direction} | {Status(x.Status)} | LV {Format(x.Leverage)}×"));
    }

    private async Task<string> BuildResultsAsync(CancellationToken token)
    {
        var results = (await store.GetHistoryAsync(30, 100, token))
            .Where(x => x.Signal.CompletedAtUtc is not null || x.Signal.Status == "Expired")
            .OrderByDescending(x => x.Signal.CompletedAtUtc ?? x.UpdatedAtUtc).Take(5).ToArray();
        if (results.Length == 0) return "📭 هنوز نتیجه‌ای ثبت نشده است.";
        return "🏁 پنج نتیجه آخر\n\n" + string.Join("\n", results.Select(x =>
            $"• {x.Signal.Symbol} | {x.Signal.Direction} | {Outcome(x.Signal)} | {FormatTime(x.Signal.CompletedAtUtc ?? x.UpdatedAtUtc)}"));
    }

    private static string Status(string value) => value switch
    {
        "Pending" => "در انتظار ورود", "Submitting" => "در حال ارسال",
        "Submitted" => "سفارش باز", "Filled" => "پوزیشن باز", _ => value
    };

    private static string Outcome(ServerSignal signal) => signal.Outcome switch
    {
        "Target" => "تارگت", "RiskFree" => "ریسک‌فری", "StopLoss" => "استاپ",
        "Expired" => signal.ExpireReason == "InitialBoundary" ? "اکسپایر ۱" : "اکسپایر ۲",
        _ => signal.Status
    };

    private static string Format(decimal? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—";
    private static string FormatTime(DateTime? value) =>
        value?.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture) ?? "—";
}

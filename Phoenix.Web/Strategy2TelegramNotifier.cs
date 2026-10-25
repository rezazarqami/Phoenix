using System.Globalization;
using System.Net.Http.Json;

namespace Phoenix.Web;

public sealed record Strategy2TelegramOptions(string? BotToken, string? ChatId)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
    public static Strategy2TelegramOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("STRATEGY2_TELEGRAM_BOT_TOKEN"),
        Environment.GetEnvironmentVariable("STRATEGY2_TELEGRAM_CHAT_ID"));
}

public sealed class Strategy2TelegramNotifier(
    Strategy2TelegramOptions options, ILogger<Strategy2TelegramNotifier> logger, Strategy2Runtime runtime)
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public Task QueuedAsync(ServerSignal s, CancellationToken token) => SendAsync(
        $"🧪 سیگنال وارد صف Strategy 2 شد\n{Describe(s)}", token);
    public Task EnteredAsync(ServerSignal s, CancellationToken token) => SendAsync(
        $"🚀 Strategy 2 با موجودی قابل‌استفاده وارد شد\n{Describe(s)}\nموجودی مصرف‌شده: {F(s.PositionSizeUsdt)} USDT", token);
    public Task ExpiredAsync(ServerSignal s, CancellationToken token) => SendAsync(
        $"⌛ سیگنال Strategy 2 اکسپایر شد\n{Describe(s)}\nدلیل: {ExpireReason(s.ExpireReason)}", token);
    public Task RiskFreeAsync(ServerSignal s, CancellationToken token) => SendAsync(
        $"🛡️ Strategy 2 به منطقه Risk Free رسید\n{Describe(s)}\nSL2: {F(s.StopLoss2)}", token);
    public async Task TargetAsync(ServerSignal s, CancellationToken token) => await SendAsync(
        $"🏆 Strategy 2 به تارگت رسید\n{Describe(s)}" + await WalletNotification.ReadAsync(runtime.Client, token), token);
    public async Task StopAsync(ServerSignal s, CancellationToken token) => await SendAsync(
        $"🛑 Strategy 2 به استاپ‌لاس رسید\n{Describe(s)}" + await WalletNotification.ReadAsync(runtime.Client, token), token);
    public async Task ClosedRiskFreeAsync(ServerSignal s, CancellationToken token) => await SendAsync(
        $"💚 Strategy 2 با ریسک‌فری بسته شد\n{Describe(s)}" + await WalletNotification.ReadAsync(runtime.Client, token), token);
    public Task ErrorAsync(ServerSignal s, CancellationToken token) => SendAsync(
        $"⚠️ خطای Strategy 2\n{Describe(s)}\n{s.Error}", token);

    private async Task SendAsync(string text, CancellationToken token)
    {
        if (!options.IsConfigured) return;
        try
        {
            using var response = await Client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{options.BotToken}/sendMessage",
                new { chat_id = options.ChatId, text }, token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception) { logger.LogWarning(exception, "Strategy 2 Telegram notification failed"); }
    }

    private static string Describe(ServerSignal s) =>
        $"نماد: {s.Symbol}\nجهت: {s.Direction}\nEntry: {F(s.EntryPrice)}\nTP: {F(s.TakeProfit)}\nSL: {F(s.StopLoss)}\nLeverage: {F(s.Leverage)}×";
    private static string ExpireReason(string? reason) => reason switch
    {
        "PositionAlreadyOpen" => "در زمان رسیدن به Entry، پوزیشن دیگری باز بود",
        "InitialBoundary" => "عبور از مرز اولیه",
        "TargetAfterActivation" => "بازگشت به تارگت پس از فعال‌شدن اکسپایر",
        _ => reason ?? "نامشخص"
    };
    private static string F(decimal? value) =>
        value?.ToString("0.################", CultureInfo.InvariantCulture) ?? "—";
}

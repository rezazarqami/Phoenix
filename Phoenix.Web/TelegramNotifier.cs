using System.Globalization;
using System.Text.Json;

namespace Phoenix.Web;

public sealed record TelegramOptions(string? BotToken, string? ChatId)
{
    public bool HasToken => !string.IsNullOrWhiteSpace(BotToken);

    public static TelegramOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"),
        Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));
}

public sealed class TelegramNotifier(TelegramOptions options, ILogger<TelegramNotifier> logger)
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _chatGate = new(1, 1);
    private string? _chatId = options.ChatId;

    public bool IsConfigured => options.HasToken;

    public Task<bool> SignalQueuedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"🆕 سیگنال جدید وارد Phoenix شد\n{Describe(signal)}\nزمان: {FormatTime(signal.CreatedAtUtc)}", token);

    public Task<bool> EntryReachedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"🎯 قیمت به نقطه ورود رسید\n{Describe(signal)}\nقیمت لحظه‌ای: {Format(signal.LastPrice)}", token);

    public Task<bool> OrderSubmittedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"✅ سفارش در Bybit Demo پذیرفته شد\n{Describe(signal)}\nشناسه سفارش: {signal.BybitOrderId}", token);

    public Task<bool> TargetReachedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"🏆 قیمت به تارگت رسید\n{Describe(signal)}\nقیمت لحظه‌ای: {Format(signal.LastPrice)}", token);

    public Task<bool> RiskFreeReachedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"🛡️ مرحله ریسک‌فری / SL2 فعال شد\n{Describe(signal)}\nSL2: {Format(signal.StopLoss2)}\nقیمت لحظه‌ای: {Format(signal.LastPrice)}", token);

    public Task<bool> StopLossReachedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"🛑 قیمت به سطح استاپ‌لاس رسید\n{Describe(signal)}\nقیمت لحظه‌ای: {Format(signal.LastPrice)}", token);

    public Task<bool> OrderErrorAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"⚠️ خطای ارسال سفارش Demo\n{Describe(signal)}\nخطا: {signal.Error}", token);

    public Task<bool> RemovedAsync(ServerSignal signal, bool cancelledAtBybit, CancellationToken token) => SendAsync(
        cancelledAtBybit
            ? $"🚫 سفارش Demo لغو شد\n{Describe(signal)}"
            : $"🗑️ سیگنال از صف حذف شد\n{Describe(signal)}", token);

    public async Task<bool> SendTestAsync(CancellationToken token) =>
        await SendAsync("✅ اتصال اعلان‌های Telegram به Phoenix برقرار شد.", token);

    private async Task<bool> SendAsync(string text, CancellationToken token)
    {
        if (!options.HasToken)
            return false;

        try
        {
            var chatId = await ResolveChatIdAsync(token);
            if (string.IsNullOrWhiteSpace(chatId))
            {
                logger.LogWarning("Telegram bot has no destination. Send /start to the bot first.");
                return false;
            }
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = text,
                ["disable_web_page_preview"] = "true"
            });
            using var response = await _httpClient.PostAsync(
                $"https://api.telegram.org/bot{options.BotToken}/sendMessage", content, token);
            var json = await response.Content.ReadAsStringAsync(token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                return true;
            throw new InvalidOperationException("Telegram Bot API did not confirm the message.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Telegram notification failed");
            return false;
        }
    }

    private async Task<string?> ResolveChatIdAsync(CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(_chatId))
            return _chatId;

        await _chatGate.WaitAsync(token);
        try
        {
            if (!string.IsNullOrWhiteSpace(_chatId))
                return _chatId;

            using var response = await _httpClient.GetAsync(
                $"https://api.telegram.org/bot{options.BotToken}/getUpdates?limit=100", token);
            var json = await response.Content.ReadAsStringAsync(token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                return null;

            foreach (var update in document.RootElement.GetProperty("result").EnumerateArray().Reverse())
            {
                if (!update.TryGetProperty("message", out var message) ||
                    !message.TryGetProperty("chat", out var chat))
                    continue;
                var type = chat.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (type is not ("private" or "group" or "supergroup" or "channel"))
                    continue;
                _chatId = chat.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
                return _chatId;
            }
            return null;
        }
        finally
        {
            _chatGate.Release();
        }
    }

    private static string Describe(ServerSignal signal) =>
        $"نماد: {signal.Symbol}\nجهت: {signal.Direction}\nورود: {Format(signal.EntryPrice)}\nتارگت: {Format(signal.TakeProfit)}\nاستاپ: {Format(signal.StopLoss)}\nسرمایه دمو: {Format(signal.PositionSizeUsdt)} USDT";

    private static string Format(decimal? value) => value?.ToString("0.################", CultureInfo.InvariantCulture) ?? "—";
    private static string FormatTime(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}

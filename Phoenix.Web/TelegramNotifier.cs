using System.Globalization;
using System.Text.Json;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed record TelegramOptions(string? BotToken, string? ChatId)
{
    public bool HasToken => !string.IsNullOrWhiteSpace(BotToken);

    public static TelegramOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"),
        Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));
}

public sealed class TelegramNotifier(TelegramOptions options, BybitDemoOptions bybitOptions,
    ILogger<TelegramNotifier> logger)
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _chatGate = new(1, 1);
    private string? _chatId = options.ChatId;

    public bool IsConfigured => options.HasToken;

    public bool IsAuthorizedChat(string chatId) =>
        !string.IsNullOrWhiteSpace(_chatId) && string.Equals(_chatId, chatId, StringComparison.Ordinal);

    public Task<bool> SignalQueuedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"🆕 سیگنال جدید وارد Phoenix شد\n{Describe(signal)}\nزمان: {FormatTime(signal.CreatedAtUtc)}", token);

    public Task<bool> EntryReachedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"🎯 قیمت به نقطه ورود رسید\n{Describe(signal)}\nقیمت لحظه‌ای: {Format(signal.LastPrice)}", token);

    public Task<bool> OrderSubmittedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"✅ سفارش در Bybit {bybitOptions.EnvironmentName} پذیرفته شد\n{Describe(signal)}\nشناسه سفارش: {signal.BybitOrderId}", token);

    public Task<bool> TargetReachedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"🏆 قیمت به تارگت رسید\n{Describe(signal)}\nقیمت لحظه‌ای: {Format(signal.LastPrice)}", token);

    public Task<bool> RiskFreeReachedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"🛡️ مرحله ریسک‌فری / SL2 فعال شد\n{Describe(signal)}\nSL2: {Format(signal.StopLoss2)}\nقیمت لحظه‌ای: {Format(signal.LastPrice)}", token);

    public Task<bool> RiskFreeClosedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"💚 معامله با ریسک‌فری بسته شد\n{Describe(signal)}\nقیمت خروج SL2: {Format(signal.StopLoss2)}", token);

    public Task<bool> StopLossReachedAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"🛑 قیمت به سطح استاپ‌لاس رسید\n{Describe(signal)}\nقیمت لحظه‌ای: {Format(signal.LastPrice)}", token);

    public Task<bool> OrderErrorAsync(ServerSignal signal, CancellationToken token) => SendAsync(
        $"⚠️ خطای ارسال سفارش {bybitOptions.EnvironmentName}\n{Describe(signal)}\nخطا: {signal.Error}", token);

    public Task<bool> RemovedAsync(ServerSignal signal, bool cancelledAtBybit, CancellationToken token) => SendAsync(
        cancelledAtBybit
            ? $"🚫 سفارش {bybitOptions.EnvironmentName} لغو شد\n{Describe(signal)}"
            : $"🗑️ سیگنال از صف حذف شد\n{Describe(signal)}", token);

    public async Task<bool> SendTestAsync(CancellationToken token) =>
        await SendAsync("✅ اتصال اعلان‌های Telegram به Phoenix برقرار شد.", token);

    public Task<bool> SendCommandReplyAsync(string chatId, string text, CancellationToken token) =>
        IsAuthorizedChat(chatId) ? SendToChatAsync(chatId, text, token) : Task.FromResult(false);

    public async Task ConfigureMenuAsync(CancellationToken token)
    {
        if (!options.HasToken) return;
        var commands = JsonSerializer.Serialize(new
        {
            commands = new[]
            {
                new { command = "status", description = "وضعیت اتصال و موتور Phoenix" },
                new { command = "active", description = "سیگنال‌های فعال و در انتظار" },
                new { command = "results", description = "۵ نتیجه آخر" },
                new { command = "help", description = "راهنمای ربات" }
            }
        });
        await PostJsonAsync("setMyCommands", commands, token);
        await PostJsonAsync("setChatMenuButton", JsonSerializer.Serialize(new
        {
            menu_button = new { type = "commands" }
        }), token);
        await PostJsonAsync("setMyDescription", JsonSerializer.Serialize(new
        {
            description = $"دستیار خصوصی Phoenix برای اعلان مراحل سیگنال‌ها، مشاهده وضعیت موتور و نتایج معاملات {bybitOptions.EnvironmentName}."
        }), token);
        await PostJsonAsync("setMyShortDescription", JsonSerializer.Serialize(new
        {
            short_description = "اعلان و پایش سیگنال‌های Phoenix"
        }), token);
    }

    public async Task<IReadOnlyList<TelegramCommand>> GetCommandsAsync(long offset, CancellationToken token)
    {
        if (!options.HasToken) return Array.Empty<TelegramCommand>();
        using var response = await _httpClient.GetAsync(
            $"https://api.telegram.org/bot{options.BotToken}/getUpdates?offset={offset}&timeout=25&allowed_updates=%5B%22message%22%5D", token);
        var json = await response.Content.ReadAsStringAsync(token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.GetProperty("ok").GetBoolean()) return Array.Empty<TelegramCommand>();
        var result = new List<TelegramCommand>();
        foreach (var update in document.RootElement.GetProperty("result").EnumerateArray())
        {
            if (!update.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("text", out var textElement) ||
                !message.TryGetProperty("chat", out var chat)) continue;
            var text = textElement.GetString();
            if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('/')) continue;
            result.Add(new TelegramCommand(
                update.GetProperty("update_id").GetInt64(),
                chat.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture),
                text.Split('@', 2)[0].Split(' ', 2)[0].ToLowerInvariant()));
        }
        return result;
    }

    public async Task<long> GetInitialUpdateOffsetAsync(CancellationToken token)
    {
        if (!options.HasToken) return 0;
        using var response = await _httpClient.GetAsync(
            $"https://api.telegram.org/bot{options.BotToken}/getUpdates?offset=-1&limit=1&timeout=0", token);
        var json = await response.Content.ReadAsStringAsync(token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        var updates = document.RootElement.GetProperty("result");
        return updates.GetArrayLength() == 0 ? 0 : updates[0].GetProperty("update_id").GetInt64() + 1;
    }

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
            return await SendToChatAsync(chatId, text, token);
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

    private async Task<bool> SendToChatAsync(string chatId, string text, CancellationToken token)
    {
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
        return document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
    }

    private async Task PostJsonAsync(string method, string json, CancellationToken token)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            $"https://api.telegram.org/bot{options.BotToken}/{method}", content, token);
        response.EnsureSuccessStatusCode();
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
        $"نماد: {signal.Symbol}\nجهت: {signal.Direction}\nورود: {Format(signal.EntryPrice)}\nتارگت: {Format(signal.TakeProfit)}\nاستاپ: {Format(signal.StopLoss)}\nمقدار ورودی: {Format(signal.PositionSizeUsdt)} USDT";

    private static string Format(decimal? value) => value?.ToString("0.################", CultureInfo.InvariantCulture) ?? "—";
    private static string FormatTime(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}

public sealed record TelegramCommand(long UpdateId, string ChatId, string Command);

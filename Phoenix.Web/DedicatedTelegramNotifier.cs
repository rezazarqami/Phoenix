using System.Globalization;
using System.Text.Json;

namespace Phoenix.Web;

public sealed record DedicatedTelegramOptions(string Username, string? BotToken, string? ChatId)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);

    public static DedicatedTelegramOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("DEDICATED_TELEGRAM_USERNAME")?.Trim() is { Length: > 0 } username
            ? username : "arman",
        Environment.GetEnvironmentVariable("DEDICATED_TELEGRAM_BOT_TOKEN"),
        Environment.GetEnvironmentVariable("DEDICATED_TELEGRAM_CHAT_ID"));

    public bool Owns(string? username) =>
        !string.IsNullOrWhiteSpace(username) &&
        string.Equals(Username, username, StringComparison.OrdinalIgnoreCase);
}

public sealed class DedicatedTelegramNotifier(
    DedicatedTelegramOptions options,
    ILogger<DedicatedTelegramNotifier> logger,
    HttpClient? httpClient = null)
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(35) };
    private readonly HttpClient _httpClient = httpClient ?? Client;

    public bool IsConfigured => options.IsConfigured;
    public bool Owns(string? username) => options.Owns(username);

    public async Task<bool> SendCandidateAsync(byte[] image, string caption, string key, CancellationToken token)
    {
        if (!options.IsConfigured) return false;
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(options.ChatId!), "chat_id");
        content.Add(new StringContent(caption), "caption");
        content.Add(new StringContent(JsonSerializer.Serialize(new { inline_keyboard = new[] { new[] {
            new { text = "✅ تأیید و ثبت", callback_data = $"batch:yes:{key}" },
            new { text = "❌ رد", callback_data = $"batch:no:{key}" }
        } } })), "reply_markup");
        var photo = new ByteArrayContent(image);
        photo.Headers.ContentType = new("image/png");
        content.Add(photo, "photo", $"signal-{key}.png");
        using var response = await _httpClient.PostAsync(
            $"https://api.telegram.org/bot{options.BotToken}/sendPhoto", content, token);
        if (response.IsSuccessStatusCode) return true;
        logger.LogWarning("Dedicated Telegram candidate failed: {Status}", response.StatusCode);
        return false;
    }

    public async Task<IReadOnlyList<TelegramCommand>> GetCommandsAsync(long offset, CancellationToken token)
    {
        if (!options.IsConfigured) return Array.Empty<TelegramCommand>();
        using var response = await _httpClient.GetAsync(
            $"https://api.telegram.org/bot{options.BotToken}/getUpdates?offset={offset}&timeout=25&allowed_updates=%5B%22message%22,%22callback_query%22%5D", token);
        var json = await response.Content.ReadAsStringAsync(token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.GetProperty("ok").GetBoolean()) return Array.Empty<TelegramCommand>();
        var result = new List<TelegramCommand>();
        foreach (var update in document.RootElement.GetProperty("result").EnumerateArray())
        {
            if (update.TryGetProperty("callback_query", out var callback) &&
                callback.TryGetProperty("data", out var callbackData) &&
                callback.TryGetProperty("message", out var message) &&
                message.TryGetProperty("chat", out var chat) &&
                callback.TryGetProperty("from", out var from))
            {
                result.Add(new TelegramCommand(update.GetProperty("update_id").GetInt64(),
                    chat.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture),
                    from.GetProperty("id").GetInt64(), GetOptionalString(from, "username"),
                    GetDisplayName(from), callbackData.GetString() ?? string.Empty,
                    callback.GetProperty("id").GetString()));
                continue;
            }
            if (!update.TryGetProperty("message", out message) ||
                !message.TryGetProperty("text", out var textElement) ||
                !message.TryGetProperty("chat", out chat)) continue;
            var text = textElement.GetString();
            if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('/')) continue;
            result.Add(new TelegramCommand(update.GetProperty("update_id").GetInt64(),
                chat.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture),
                message.TryGetProperty("from", out from) ? from.GetProperty("id").GetInt64() : 0,
                message.TryGetProperty("from", out from) ? GetOptionalString(from, "username") : null,
                message.TryGetProperty("from", out from) ? GetDisplayName(from) : "Telegram user",
                text.Split('@', 2)[0].Split(' ', 2)[0].ToLowerInvariant(), null));
        }
        return result;
    }

    public bool IsAuthorized(TelegramCommand command) =>
        options.IsConfigured && string.Equals(command.ChatId, options.ChatId, StringComparison.Ordinal);

    public async Task AnswerCallbackAsync(string callbackId, string text, CancellationToken token) =>
        await PostJsonAsync("answerCallbackQuery",
            JsonSerializer.Serialize(new { callback_query_id = callbackId, text }), token);

    public async Task SendWelcomeAsync(CancellationToken token) =>
        await PostJsonAsync("sendMessage", JsonSerializer.Serialize(new
        {
            chat_id = options.ChatId,
            text = "✅ ربات اختصاصی سیگنال‌های Phoenix فعال است. پیشنهادهای این حساب و نتیجه همان سیگنال‌ها در اینجا نمایش داده می‌شود."
        }), token);

    public async Task<long> GetInitialUpdateOffsetAsync(CancellationToken token)
    {
        if (!options.IsConfigured) return 0;
        using var response = await _httpClient.GetAsync(
            $"https://api.telegram.org/bot{options.BotToken}/getUpdates?offset=-1&limit=1&timeout=0", token);
        var json = await response.Content.ReadAsStringAsync(token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        var updates = document.RootElement.GetProperty("result");
        return updates.GetArrayLength() == 0 ? 0 : updates[0].GetProperty("update_id").GetInt64() + 1;
    }

    private async Task PostJsonAsync(string method, string json, CancellationToken token)
    {
        if (!options.IsConfigured) return;
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            $"https://api.telegram.org/bot{options.BotToken}/{method}", content, token);
        response.EnsureSuccessStatusCode();
    }

    private static string? GetOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() : null;

    private static string GetDisplayName(JsonElement user)
    {
        var name = $"{GetOptionalString(user, "first_name")} {GetOptionalString(user, "last_name")}".Trim();
        return string.IsNullOrWhiteSpace(name) ? "Telegram user" : name;
    }
}

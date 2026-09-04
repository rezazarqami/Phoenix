using System.Globalization;
using System.Text.Json;

namespace Phoenix.Web;

public sealed record DedicatedTelegramOptions(string Username, string? BotToken, string? ChatId,
    string? PairingCode = null, string? ChatStorePath = null)
{
    private const string DefaultChatStorePath = "/var/lib/phoenix/dedicated-telegram-chats.json";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && GetChatIds().Count > 0;

    public static DedicatedTelegramOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("DEDICATED_TELEGRAM_USERNAME")?.Trim() is { Length: > 0 } username
            ? username : "arman",
        Environment.GetEnvironmentVariable("DEDICATED_TELEGRAM_BOT_TOKEN"),
        Environment.GetEnvironmentVariable("DEDICATED_TELEGRAM_CHAT_IDS") ??
            Environment.GetEnvironmentVariable("DEDICATED_TELEGRAM_CHAT_ID"),
        Environment.GetEnvironmentVariable("DEDICATED_TELEGRAM_PAIRING_CODE"),
        Environment.GetEnvironmentVariable("DEDICATED_TELEGRAM_CHAT_STORE"));

    public bool Owns(string? username) =>
        !string.IsNullOrWhiteSpace(username) &&
        string.Equals(Username, username, StringComparison.OrdinalIgnoreCase);

    public string StorePath => string.IsNullOrWhiteSpace(ChatStorePath) ? DefaultChatStorePath : ChatStorePath;

    public IReadOnlyList<string> GetChatIds()
    {
        var result = (ChatId ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
        try
        {
            if (File.Exists(StorePath))
                foreach (var value in JsonSerializer.Deserialize<string[]>(File.ReadAllText(StorePath)) ?? [])
                    if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
        }
        catch { }
        return result.ToArray();
    }
}

public sealed class DedicatedTelegramNotifier(
    DedicatedTelegramOptions options,
    ILogger<DedicatedTelegramNotifier> logger,
    HttpClient? httpClient = null)
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(35) };
    private readonly HttpClient _httpClient = httpClient ?? Client;
    private readonly SemaphoreSlim _pairingGate = new(1, 1);

    public bool IsConfigured => options.IsConfigured;
    public bool Owns(string? username) => options.Owns(username);

    public async Task<bool> SendCandidateAsync(byte[] image, string caption, string key, CancellationToken token)
    {
        if (!options.IsConfigured) return false;
        var sent = false;
        foreach (var chatId in options.GetChatIds())
            sent |= await SendCandidateToChatAsync(chatId, image, caption, key, token);
        return sent;
    }

    private async Task<bool> SendCandidateToChatAsync(string chatId, byte[] image, string caption, string key,
        CancellationToken token)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(chatId), "chat_id");
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
            var command = text.Trim();
            var separator = command.IndexOf(' ');
            var head = separator >= 0 ? command[..separator] : command;
            var suffix = separator >= 0 ? command[separator..] : string.Empty;
            var mention = head.IndexOf('@');
            if (mention >= 0) head = head[..mention];
            result.Add(new TelegramCommand(update.GetProperty("update_id").GetInt64(),
                chat.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture),
                message.TryGetProperty("from", out from) ? from.GetProperty("id").GetInt64() : 0,
                message.TryGetProperty("from", out from) ? GetOptionalString(from, "username") : null,
                message.TryGetProperty("from", out from) ? GetDisplayName(from) : "Telegram user",
                (head + suffix).ToLowerInvariant(), null));
        }
        return result;
    }

    public bool IsAuthorized(TelegramCommand command) =>
        options.GetChatIds().Contains(command.ChatId, StringComparer.Ordinal);

    public async Task<bool> TryPairAsync(TelegramCommand command, CancellationToken token)
    {
        if (IsAuthorized(command)) return true;
        var pieces = command.Command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length != 2 || pieces[0] != "/start" || string.IsNullOrWhiteSpace(options.PairingCode) ||
            !string.Equals(pieces[1], options.PairingCode, StringComparison.Ordinal)) return false;
        await _pairingGate.WaitAsync(token);
        try
        {
            var chats = options.GetChatIds().ToHashSet(StringComparer.Ordinal);
            if (chats.Count >= 2) return false;
            chats.Add(command.ChatId);
            var directory = Path.GetDirectoryName(options.StorePath)
                ?? throw new InvalidOperationException("Dedicated Telegram chat store path is invalid.");
            Directory.CreateDirectory(directory);
            var temporary = options.StorePath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(chats), token);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, options.StorePath, true);
            return true;
        }
        finally { _pairingGate.Release(); }
    }

    public async Task AnswerCallbackAsync(string callbackId, string text, CancellationToken token) =>
        await PostJsonAsync("answerCallbackQuery",
            JsonSerializer.Serialize(new { callback_query_id = callbackId, text }), token);

    public async Task SendWelcomeAsync(string chatId, CancellationToken token) =>
        await PostJsonAsync("sendMessage", JsonSerializer.Serialize(new
        {
            chat_id = chatId,
            text = "✅ ربات اختصاصی سیگنال‌های Phoenix فعال است. پیشنهادهای این حساب و نتیجه همان سیگنال‌ها در اینجا نمایش داده می‌شود."
        }), token);

    public async Task SendCommandReplyAsync(string chatId, string text, CancellationToken token) =>
        await PostJsonAsync("sendMessage", JsonSerializer.Serialize(new { chat_id = chatId, text }), token);

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

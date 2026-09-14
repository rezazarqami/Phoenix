using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Phoenix.Web;

public sealed record PublicSignalTelegramOptions(string? BotToken, string? ChatId)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);

    public static PublicSignalTelegramOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("PUBLIC_TELEGRAM_BOT_TOKEN"),
        Environment.GetEnvironmentVariable("PUBLIC_TELEGRAM_CHAT_ID"));
}

public sealed class PublicSignalNotifier
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly PublicSignalTelegramOptions _options;
    private readonly DedicatedTelegramOptions _dedicatedOptions;
    private readonly ILogger<PublicSignalNotifier> _logger;
    private readonly HttpClient? _httpClient;

    public PublicSignalNotifier(PublicSignalTelegramOptions options,
        ILogger<PublicSignalNotifier> logger, HttpClient? httpClient = null)
        : this(options, new DedicatedTelegramOptions("arman", null, null), logger, httpClient) { }

    public PublicSignalNotifier(PublicSignalTelegramOptions options,
        DedicatedTelegramOptions dedicatedOptions,
        ILogger<PublicSignalNotifier> logger, HttpClient? httpClient = null)
    {
        _options = options;
        _dedicatedOptions = dedicatedOptions;
        _logger = logger;
        _httpClient = httpClient;
    }

    public bool IsDedicatedSignal(ServerSignal signal) =>
        _dedicatedOptions.Owns(signal.RequestedByUsername);

    public async Task<int?> PublishAsync(ServerSignal signal, CancellationToken token)
    {
        if (signal.PublicSignalNumber is null) return null;
        if (IsDedicatedSignal(signal) ? !_dedicatedOptions.IsConfigured : !_options.IsConfigured) return null;
        var directionIcon = signal.Direction == "Long" ? "🟢" : "🔴";
        var symbol = signal.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
            ? $"{signal.Symbol[..^4]}/USDT"
            : signal.Symbol;
        var text = $"""
            📊 Signal NO : {signal.PublicSignalNumber.Value:0000}  (CRYPTO)

            ✅️ FUTURES
            *(CROSS)

            {directionIcon} {signal.Direction.ToUpperInvariant()}:
            🔵 LEVERAGE: {Format(signal.Leverage ?? 0)}

            {symbol}

            ENTRY  ~  {Format(signal.EntryPrice)}   (2% از کل سرمایه)

            TP: {Format(signal.TakeProfit)}

            ⛔️Stop loss: {Format(signal.StopLoss)}

            (‌1%- از کل سرمایه)
            """;
        return IsDedicatedSignal(signal)
            ? await SendDedicatedAsync(text, token)
            : await SendAsync(_options, text, null, token);
    }

    public Task<int?> RiskFreeReachedAsync(ServerSignal signal, CancellationToken token) =>
        ReplyAsync(signal, "✅ این سیگنال به منطقه Risk Free رسید.", token);

    public Task<int?> TargetReachedAsync(ServerSignal signal, CancellationToken token) =>
        ReplyAsync(signal, $"🏆 سیگنال {signal.Symbol} به تارگت رسید.", token);

    public Task<int?> TargetReachedAsync(ServerSignal signal, byte[]? image, CancellationToken token) =>
        ReplyResultAsync(signal, $"🏆 <b>سیگنال {signal.Symbol} به تارگت رسید.</b>", image, token);

    public Task<int?> StopLossReachedAsync(ServerSignal signal, CancellationToken token) =>
        ReplyAsync(signal, $"🛑 سیگنال {signal.Symbol} به استاپ‌لاس رسید.", token);

    public Task<int?> StopLossReachedAsync(ServerSignal signal, byte[]? image, CancellationToken token) =>
        ReplyResultAsync(signal, $"🛑 <b>سیگنال {signal.Symbol} به استاپ‌لاس رسید.</b>", image, token);

    public Task<int?> ExpiredAsync(ServerSignal signal, CancellationToken token)
    {
        if (IsDedicatedSignal(signal))
        {
            var text = signal.ExpireReason == "InitialBoundary"
                ? $"⌛ سیگنال {signal.Symbol} پیش از ورود اکسپایر شد."
                : signal.ExpireReason == "TargetAfterActivation"
                    ? $"⌛ سیگنال {signal.Symbol} پس از نزدیک‌شدن به ورود و بازگشت به تارگت اکسپایر شد."
                    : $"⌛ سیگنال {signal.Symbol} اکسپایر شد.";
            return ReplyAsync(signal, text, token);
        }
        return signal.ExpireReason == "TargetAfterActivation"
            ? ReplyAsync(signal, $"⌛ سیگنال {signal.Symbol} پس از نزدیک‌شدن به ورود و بازگشت به تارگت اکسپایر شد.", token)
            : Task.FromResult<int?>(null);
    }

    public Task<int?> OpenedAsync(ServerSignal signal, CancellationToken token) =>
        ReplyAsync(signal, $"▶️ معامله {signal.Symbol} باز شد.", token);

    public Task<int?> RiskFreeClosedAsync(ServerSignal signal, CancellationToken token) =>
        ReplyAsync(signal, $"✅ معامله {signal.Symbol} با ریسک‌فری بسته شد.", token);

    public Task<int?> ManuallyClosedAsync(ServerSignal signal, CancellationToken token) =>
        IsDedicatedSignal(signal)
            ? ReplyAsync(signal, $"⏹ معامله {signal.Symbol} به‌صورت دستی بسته شد.", token)
            : Task.FromResult<int?>(null);

    public Task<int?> CancelledAsync(ServerSignal signal, CancellationToken token) =>
        IsDedicatedSignal(signal)
            ? ReplyAsync(signal, $"⏹ سیگنال {signal.Symbol} لغو شد.", token)
            : Task.FromResult<int?>(null);

    private Task<int?> ReplyAsync(ServerSignal signal, string text, CancellationToken token)
    {
        if (IsDedicatedSignal(signal)) return SendDedicatedAsync(text, token);
        return signal.PublicTelegramMessageId is > 0
            ? SendAsync(_options, text, signal.PublicTelegramMessageId, token)
            : Task.FromResult<int?>(null);
    }

    private Task<int?> ReplyResultAsync(ServerSignal signal, string headline, byte[]? image,
        CancellationToken token)
    {
        var scores = signal.TargetSimilarityPercent.HasValue
            ? $"\n\n🟢 <b>درصد تارگت زمان صدور: {Format(signal.TargetSimilarityPercent.Value)}٪</b>\n🔴 <b>درصد استاپ زمان صدور: {Format(signal.StopSimilarityPercent ?? 0m)}٪</b>"
            : "\n\n📊 درصد زمان صدور: دادهٔ کافی نبود";
        var text = headline + scores;
        if (image is null) return ReplyAsync(signal, text.Replace("<b>", "").Replace("</b>", ""), token);
        return IsDedicatedSignal(signal)
            ? SendDedicatedPhotoAsync(text, image, token)
            : signal.PublicTelegramMessageId is > 0
                ? SendPhotoAsync(_options, text, image, signal.PublicTelegramMessageId, token)
                : Task.FromResult<int?>(null);
    }

    private async Task<int?> SendDedicatedAsync(string text, CancellationToken token)
    {
        int? firstMessageId = null;
        foreach (var chatId in _dedicatedOptions.GetChatIds())
        {
            var messageId = await SendAsync(
                new PublicSignalTelegramOptions(_dedicatedOptions.BotToken, chatId), text, null, token);
            firstMessageId ??= messageId;
        }
        return firstMessageId;
    }

    private async Task<int?> SendDedicatedPhotoAsync(string text, byte[] image, CancellationToken token)
    {
        int? firstMessageId = null;
        foreach (var chatId in _dedicatedOptions.GetChatIds())
        {
            var messageId = await SendPhotoAsync(
                new PublicSignalTelegramOptions(_dedicatedOptions.BotToken, chatId), text, image, null, token);
            firstMessageId ??= messageId;
        }
        return firstMessageId;
    }

    private async Task<int?> SendPhotoAsync(PublicSignalTelegramOptions destination, string caption,
        byte[] image, int? replyToMessageId, CancellationToken token)
    {
        if (!destination.IsConfigured) return null;
        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(destination.ChatId!), "chat_id");
            content.Add(new StringContent(caption), "caption");
            content.Add(new StringContent("HTML"), "parse_mode");
            if (replyToMessageId is { } messageId)
                content.Add(new StringContent(JsonSerializer.Serialize(new { message_id = messageId })), "reply_parameters");
            var photo = new ByteArrayContent(image);
            photo.Headers.ContentType = new("image/png");
            content.Add(photo, "photo", "signal-result.png");
            using var response = await (_httpClient ?? Client).PostAsync(
                $"https://api.telegram.org/bot{destination.BotToken}/sendPhoto", content, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("result").GetProperty("message_id").GetInt32();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Public Telegram result image failed");
            return null;
        }
    }

    private async Task<int?> SendAsync(PublicSignalTelegramOptions destination, string text,
        int? replyToMessageId, CancellationToken token)
    {
        if (!destination.IsConfigured) return null;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = destination.ChatId,
                ["text"] = text
            };
            if (replyToMessageId is { } messageId)
                payload["reply_parameters"] = new { message_id = messageId };
            using var response = await (_httpClient ?? Client).PostAsJsonAsync(
                $"https://api.telegram.org/bot{destination.BotToken}/sendMessage", payload, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Public Telegram signal failed: {Status} {Body}", response.StatusCode, body);
                return null;
            }
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("result").GetProperty("message_id").GetInt32();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Public Telegram signal failed");
            return null;
        }
    }

    private static string Format(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}

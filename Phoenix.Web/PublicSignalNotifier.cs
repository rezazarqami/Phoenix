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

public sealed class PublicSignalNotifier(
    PublicSignalTelegramOptions options,
    ILogger<PublicSignalNotifier> logger)
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<int?> PublishAsync(ServerSignal signal, CancellationToken token)
    {
        if (!options.IsConfigured || signal.PublicSignalNumber is null) return null;
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
        return await SendAsync(text, null, token);
    }

    public Task<int?> RiskFreeReachedAsync(ServerSignal signal, CancellationToken token) =>
        SendAsync("✅ این سیگنال به منطقه Risk Free رسید.", signal.PublicTelegramMessageId, token);

    private async Task<int?> SendAsync(string text, int? replyToMessageId, CancellationToken token)
    {
        if (!options.IsConfigured) return null;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = options.ChatId,
                ["text"] = text
            };
            if (replyToMessageId is { } messageId)
                payload["reply_parameters"] = new { message_id = messageId };
            using var response = await Client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{options.BotToken}/sendMessage", payload, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Public Telegram signal failed: {Status} {Body}", response.StatusCode, body);
                return null;
            }
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("result").GetProperty("message_id").GetInt32();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Public Telegram signal failed");
            return null;
        }
    }

    private static string Format(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}

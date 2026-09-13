using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Phoenix.Web;

public sealed class OpenAiOptions
{
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "gpt-5-mini";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public static OpenAiOptions FromEnvironment() => new()
    {
        ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim() ?? "",
        Model = Environment.GetEnvironmentVariable("PHOENIX_OPENAI_MODEL")?.Trim() is { Length: > 0 } model
            ? model : "gpt-5-mini"
    };
}

public sealed class AiSignalParser(HttpClient http, OpenAiOptions options)
{
    public async Task<AiSignalParseResult> ParseAsync(string? text, CancellationToken token)
    {
        text = text?.Trim();
        if (!options.IsConfigured) return AiSignalParseResult.Fail("کلید OpenAI روی سرور تنظیم نشده است.");
        if (string.IsNullOrWhiteSpace(text)) return AiSignalParseResult.Fail("متن سیگنال را وارد کنید.");
        if (text.Length > 2_000) return AiSignalParseResult.Fail("متن سیگنال بیش از حد طولانی است.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = options.Model,
            instructions = """
                Extract a trading signal without analysis, recommendation, inference, or changing numbers.
                Copy only explicitly supplied symbol, direction, high/ceiling, low/floor, and position size in USDT.
                Normalize the symbol to uppercase Bybit format and direction to Long or Short. Convert Persian digits.
                If a required field is absent or ambiguous, set complete=false and return concise Persian errors.
                Never calculate entry, target, stop, leverage, expiry, or any other trading value.
                """,
            input = text,
            text = new { format = new
            {
                type = "json_schema", name = "phoenix_signal_input", strict = true,
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        complete = new { type = "boolean" },
                        symbol = new { type = new[] { "string", "null" } },
                        direction = new { type = new[] { "string", "null" }, @enum = new object?[] { "Long", "Short", null } },
                        ceiling = new { type = new[] { "number", "null" } },
                        floor = new { type = new[] { "number", "null" } },
                        positionSizeUsdt = new { type = new[] { "number", "null" } },
                        errors = new { type = "array", items = new { type = "string" } }
                    },
                    required = new[] { "complete", "symbol", "direction", "ceiling", "floor", "positionSizeUsdt", "errors" },
                    additionalProperties = false
                }
            }}
        }), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) return AiSignalParseResult.Fail("ارتباط با سرویس AI ناموفق بود.");

        try
        {
            using var document = JsonDocument.Parse(body);
            var parsed = JsonSerializer.Deserialize<AiSignalFields>(ExtractOutputText(document.RootElement),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null) return AiSignalParseResult.Fail("پاسخ AI قابل خواندن نبود.");
            if (!parsed.Complete) return new(false, null,
                parsed.Errors.Length > 0 ? parsed.Errors : ["اطلاعات سیگنال کامل نیست."]);
            if (parsed.Symbol is null || parsed.Direction is null || parsed.Ceiling is null ||
                parsed.Floor is null || parsed.PositionSizeUsdt is null)
                return AiSignalParseResult.Fail("اطلاعات سیگنال کامل نیست.");

            var signal = new SignalRequest(parsed.Symbol, parsed.Direction, parsed.Ceiling.Value,
                parsed.Floor.Value, parsed.PositionSizeUsdt.Value);
            var error = signal.Validate();
            return error is null ? new(true, signal, []) : AiSignalParseResult.Fail(error);
        }
        catch (JsonException) { return AiSignalParseResult.Fail("پاسخ AI قابل خواندن نبود."); }
    }

    private static string ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
            return direct.GetString()!;
        foreach (var output in root.GetProperty("output").EnumerateArray())
            if (output.TryGetProperty("content", out var content))
                foreach (var item in content.EnumerateArray())
                    if (item.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString()!;
        throw new JsonException("No output text.");
    }
}

public sealed record AiSignalTextRequest(string? Text);
public sealed record AiSignalParseResult(bool Complete, SignalRequest? Signal, string[] Errors)
{
    public static AiSignalParseResult Fail(string error) => new(false, null, [error]);
}
public sealed record AiSignalFields(bool Complete, string? Symbol, string? Direction, decimal? Ceiling,
    decimal? Floor, decimal? PositionSizeUsdt, string[] Errors);

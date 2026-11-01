using System.Text.Json;
using System.Globalization;

namespace Phoenix.Web;

public sealed record CommodityQuote(string Symbol, decimal? Price, DateTimeOffset? AtUtc, bool Delayed, string Source, string? Error = null, string Currency = "USD");

// Informational homepage quotes only. Never used for orders or signal evaluation.
public sealed class CommodityQuotes(IHttpClientFactory clients, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRefresh;
    private CommodityQuote[] quotes = [new("XAU", null, null, false, "Gold API"), new("BRENT", null, null, true, "Yahoo Finance"), new("USD_TOMAN", null, null, false, "TGJU", Currency: "TOMAN"), new("GOLD18_TOMAN", null, null, false, "TGJU", Currency: "TOMAN"), new("EUR_TOMAN", null, null, false, "TGJU", Currency: "TOMAN")];

    public async Task<CommodityQuote[]> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (clock.GetUtcNow() < nextRefresh) return quotes.ToArray();
            // Cache successful and failed attempts to bound upstream traffic from all clients.
            nextRefresh = clock.GetUtcNow().AddSeconds(30);
            var previous = quotes;
            var iranTask = ReadIranAsync(previous.Where(q => q.Currency == "TOMAN").ToArray());
            var international = await Task.WhenAll(previous.Where(q => q.Currency == "USD").Select(async old =>
            {
                try
                {
                    var url = old.Symbol == "XAU" ? "https://api.gold-api.com/price/XAU" :
                        "https://query1.finance.yahoo.com/v8/finance/chart/BZ%3DF?range=1d&interval=5m";
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    using var client = clients.CreateClient("CommodityQuotes");
                    using var response = await client.GetAsync(url, deadline.Token);
                    response.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                    return Parse(old.Symbol, doc.RootElement);
                }
                catch { return old with { Error = "دریافت نرخ تازه ممکن نشد" }; }
            }));
            quotes = [.. international, .. await iranTask];
            return quotes.ToArray();
        }
        finally { gate.Release(); }
    }


    private async Task<CommodityQuote[]> ReadIranAsync(CommodityQuote[] previous)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var client = clients.CreateClient("CommodityQuotes");
            using var response = await client.GetAsync("https://call1.tgju.org/ajax.json", deadline.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            return previous.Select(old => {
                try { return ParseIran(old.Symbol, doc.RootElement); }
                catch { return old with { Error = "دریافت نرخ تازه ممکن نشد" }; }
            }).ToArray();
        }
        catch { return previous.Select(old => old with { Error = "دریافت نرخ تازه ممکن نشد" }).ToArray(); }
    }

    public static CommodityQuote ParseIran(string symbol, JsonElement data)
    {
        var key = symbol switch { "USD_TOMAN" => "price_dollar_rl", "EUR_TOMAN" => "price_eur", "GOLD18_TOMAN" => "geram18", _ => throw new ArgumentException("Unsupported Iranian asset.", nameof(symbol)) };
        var quote = data.GetProperty("current").GetProperty(key);
        var raw = quote.GetProperty("p").GetString() ?? "";
        var normalized = string.Concat(raw.Select(c => c >= '۰' && c <= '۹' ? (char)('0' + c - '۰') : c >= '٠' && c <= '٩' ? (char)('0' + c - '٠') : c)).Replace("٬", ",");
        if (!decimal.TryParse(normalized, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rial) || rial <= 0) throw new FormatException("Invalid rial quote.");
        var local = DateTime.ParseExact(quote.GetProperty("ts").GetString()!, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);
        var at = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran")));
        if (at > DateTimeOffset.UtcNow.AddMinutes(5) || at.Year < 2020) throw new FormatException("Invalid source timestamp.");
        return new(symbol, rial / 10m, at, false, "TGJU", Currency: "TOMAN");
    }

    public static CommodityQuote Parse(string symbol, JsonElement data)
    {
        decimal price; DateTimeOffset at; bool delayed; string source;
        if (symbol == "XAU")
        {
            if (data.GetProperty("symbol").GetString() != "XAU" || data.GetProperty("currency").GetString() != "USD")
                throw new FormatException("Unexpected gold quote identity or currency.");
            price = data.GetProperty("price").GetDecimal(); at = data.GetProperty("updatedAt").GetDateTimeOffset();
            delayed = false; source = "Gold API";
        }
        else if (symbol == "BRENT")
        {
            var meta = data.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
            if (meta.GetProperty("symbol").GetString() != "BZ=F" || meta.GetProperty("currency").GetString() != "USD")
                throw new FormatException("Unexpected Brent quote identity or currency.");
            price = meta.GetProperty("regularMarketPrice").GetDecimal();
            at = DateTimeOffset.FromUnixTimeSeconds(meta.GetProperty("regularMarketTime").GetInt64());
            delayed = true; source = "Yahoo Finance";
        }
        else throw new ArgumentException("Unsupported commodity.", nameof(symbol));
        if (price <= 0 || at > DateTimeOffset.UtcNow.AddMinutes(5) || at.Year < 2020) throw new FormatException("Invalid quote price or timestamp.");
        return new(symbol, price, at, delayed, source);
    }
}

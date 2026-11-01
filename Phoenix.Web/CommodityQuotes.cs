using System.Text.Json;

namespace Phoenix.Web;

public sealed record CommodityQuote(string Symbol, decimal? Price, DateTimeOffset? AtUtc, bool Delayed, string Source, string? Error = null);

// Informational homepage quotes only. Never used for orders or signal evaluation.
public sealed class CommodityQuotes(IHttpClientFactory clients, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRefresh;
    private CommodityQuote[] quotes = [new("XAU", null, null, false, "Gold API"), new("BRENT", null, null, true, "Yahoo Finance")];

    public async Task<CommodityQuote[]> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (clock.GetUtcNow() < nextRefresh) return quotes.ToArray();
            // Cache successful and failed attempts to bound upstream traffic from all clients.
            nextRefresh = clock.GetUtcNow().AddSeconds(30);
            var previous = quotes;
            quotes = await Task.WhenAll(previous.Select(async old =>
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
            return quotes.ToArray();
        }
        finally { gate.Release(); }
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

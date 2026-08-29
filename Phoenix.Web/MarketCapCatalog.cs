using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Phoenix.Web;

public sealed class MarketCapCatalog(HttpClient http, BybitInstrumentCatalog instruments, ServerOrderStore orders)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<MarketAsset> _cache = [];
    private DateTime _expiresAtUtc;

    public async Task<IReadOnlyList<MarketAsset>> GetAsync(CancellationToken token)
    {
        if (_cache.Count > 0 && DateTime.UtcNow < _expiresAtUtc) return await WithSignalsAsync(_cache, token);
        await _gate.WaitAsync(token);
        try
        {
            if (_cache.Count == 0 || DateTime.UtcNow >= _expiresAtUtc)
            {
                var tradable = await instruments.GetAsync(token);
                var markets = new List<CoinGeckoMarket>();
                try
                {
                    for (var page = 1; page <= 4; page++)
                    {
                        var rows = await http.GetFromJsonAsync<List<CoinGeckoMarket>>(
                            $"https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&order=market_cap_desc&per_page=250&page={page}&sparkline=false", token) ?? [];
                        markets.AddRange(rows);
                        if (rows.Count < 250) break;
                    }
                }
                catch (Exception) when (!token.IsCancellationRequested) { /* Keep the Bybit list usable during a market-cap provider outage. */ }
                var bySymbol = markets.Where(x => !string.IsNullOrWhiteSpace(x.Symbol))
                    .GroupBy(x => x.Symbol.ToUpperInvariant())
                    .ToDictionary(x => x.Key, x => x.OrderBy(row => row.MarketCapRank ?? int.MaxValue).First());
                _cache = tradable.Select(symbol => Map(symbol, bySymbol))
                    .OrderBy(x => x.MarketCapRank ?? int.MaxValue).ThenByDescending(x => x.MarketCap ?? 0m)
                    .ThenBy(x => x.Symbol, StringComparer.Ordinal).ToArray();
                _expiresAtUtc = DateTime.UtcNow.AddMinutes(30);
            }
            return await WithSignalsAsync(_cache, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<MarketAsset>> WithSignalsAsync(IReadOnlyList<MarketAsset> assets, CancellationToken token)
    {
        var active = (await orders.GetAllAsync(token))
            .Where(x => x.Status is "Pending" or "Submitting" or "Submitted" or "Filled")
            .GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new
            {
                Long = group.Count(x => x.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase)),
                Short = group.Count(x => x.Direction.Equals("Short", StringComparison.OrdinalIgnoreCase))
            }, StringComparer.OrdinalIgnoreCase);
        return assets.Select(asset => active.TryGetValue(asset.Symbol, out var count)
            ? asset with { ActiveLong = count.Long, ActiveShort = count.Short }
            : asset).ToArray();
    }

    private static MarketAsset Map(string symbol, IReadOnlyDictionary<string, CoinGeckoMarket> markets)
    {
        var baseSymbol = symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? symbol[..^4] : symbol;
        var candidates = new List<string> { baseSymbol };
        foreach (var prefix in new[] { "1000000", "10000", "1000" })
            if (baseSymbol.StartsWith(prefix, StringComparison.Ordinal) && baseSymbol.Length > prefix.Length)
                candidates.Add(baseSymbol[prefix.Length..]);
        var market = candidates.Select(key => markets.GetValueOrDefault(key)).FirstOrDefault(x => x is not null);
        return new MarketAsset(symbol, baseSymbol, market?.Name ?? baseSymbol, market?.MarketCapRank,
            market?.MarketCap, market?.Image, 0, 0);
    }

    private sealed record CoinGeckoMarket(
        string Id, string Symbol, string Name, string? Image,
        [property: JsonPropertyName("market_cap")] decimal? MarketCap,
        [property: JsonPropertyName("market_cap_rank")] int? MarketCapRank);
}

public sealed record MarketAsset(string Symbol, string BaseSymbol, string Name, int? MarketCapRank,
    decimal? MarketCap, string? Image, int ActiveLong, int ActiveShort)
{
    public int ActiveCount => ActiveLong + ActiveShort;
}

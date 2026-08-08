using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Phoenix.Engine.Exchanges.Bybit;

public sealed class BybitTestnetClient
{
    private readonly HttpClient _httpClient;
    private readonly BybitTestnetOptions _options;
    private readonly Func<long> _timestampProvider;

    public BybitTestnetClient(
        BybitTestnetOptions options,
        HttpClient? httpClient = null,
        Func<long>? timestampProvider = null)
    {
        _options = options;
        _httpClient = httpClient ?? new HttpClient();
        _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _httpClient.BaseAddress = new Uri(BybitTestnetOptions.BaseUrl, UriKind.Absolute);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<BybitTicker> GetLastPriceAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        symbol = NormalizeSymbol(symbol);
        var path = $"/v5/market/tickers?category=linear&symbol={Uri.EscapeDataString(symbol)}";
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        var item = document.RootElement.GetProperty("result").GetProperty("list")[0];
        var returnedSymbol = item.GetProperty("symbol").GetString() ?? symbol;
        var lastPriceText = item.GetProperty("lastPrice").GetString();

        if (!decimal.TryParse(lastPriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var lastPrice))
            throw new InvalidOperationException("Bybit returned an invalid last price.");

        return new BybitTicker(returnedSymbol, lastPrice, DateTime.UtcNow);
    }

    public async Task<BybitInstrumentRules> GetInstrumentRulesAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        symbol = NormalizeSymbol(symbol);
        var path = $"/v5/market/instruments-info?category=linear&symbol={Uri.EscapeDataString(symbol)}";
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        var item = document.RootElement.GetProperty("result").GetProperty("list")[0];
        var priceFilter = item.GetProperty("priceFilter");
        var lotFilter = item.GetProperty("lotSizeFilter");

        return new BybitInstrumentRules(
            item.GetProperty("symbol").GetString() ?? symbol,
            ReadDecimal(priceFilter, "tickSize"),
            ReadDecimal(lotFilter, "qtyStep"),
            ReadDecimal(lotFilter, "minOrderQty"),
            ReadDecimal(lotFilter, "minNotionalValue"));
    }

    public async Task<BybitTestnetStatus> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var publicResponse = await _httpClient.GetAsync("/v5/market/time", cancellationToken);
        var publicJson = await publicResponse.Content.ReadAsStringAsync(cancellationToken);
        publicResponse.EnsureSuccessStatusCode();
        using (var publicDocument = JsonDocument.Parse(publicJson))
            EnsureSuccess(publicDocument.RootElement);

        if (!_options.HasCredentials)
            return new BybitTestnetStatus(true, false, null, "Public API connected; Testnet API keys are not configured.");

        const string query = "accountType=UNIFIED&coin=USDT";
        using var request = CreateSignedGetRequest("/v5/account/wallet-balance", query);
        using var privateResponse = await _httpClient.SendAsync(request, cancellationToken);
        var privateJson = await privateResponse.Content.ReadAsStringAsync(cancellationToken);
        privateResponse.EnsureSuccessStatusCode();

        using var privateDocument = JsonDocument.Parse(privateJson);
        EnsureSuccess(privateDocument.RootElement);
        var account = privateDocument.RootElement.GetProperty("result").GetProperty("list")[0];
        var equityText = account.GetProperty("totalEquity").GetString();
        decimal? equity = decimal.TryParse(equityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value : null;

        return new BybitTestnetStatus(true, true, equity, "Bybit Testnet authentication succeeded.");
    }

    public HttpRequestMessage CreateSignedGetRequest(string path, string queryString)
    {
        if (!_options.HasCredentials)
            throw new InvalidOperationException("Bybit Testnet API credentials are not configured.");

        if (!path.StartsWith("/v5/", StringComparison.Ordinal))
            throw new InvalidOperationException("Only Bybit V5 endpoints are allowed.");

        var timestamp = _timestampProvider().ToString(CultureInfo.InvariantCulture);
        var receiveWindow = BybitTestnetOptions.ReceiveWindowMilliseconds.ToString(CultureInfo.InvariantCulture);
        var payload = timestamp + _options.ApiKey + receiveWindow + queryString;
        var signature = BybitSignature.CreateHmacSha256(_options.ApiSecret!, payload);
        var request = new HttpRequestMessage(HttpMethod.Get, $"{path}?{queryString}");
        request.Headers.Add("X-BAPI-API-KEY", _options.ApiKey);
        request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
        request.Headers.Add("X-BAPI-RECV-WINDOW", receiveWindow);
        request.Headers.Add("X-BAPI-SIGN", signature);
        return request;
    }

    private static string NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol is required.", nameof(symbol));

        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("Symbol can only contain ASCII letters and digits.", nameof(symbol));
        return normalized;
    }

    private static void EnsureSuccess(JsonElement root)
    {
        var code = root.GetProperty("retCode").GetInt32();
        if (code == 0)
            return;

        var message = root.TryGetProperty("retMsg", out var value) ? value.GetString() : "Unknown Bybit error";
        throw new InvalidOperationException($"Bybit API error {code}: {message}");
    }

    private static decimal ReadDecimal(JsonElement element, string propertyName)
    {
        var text = element.GetProperty(propertyName).GetString();
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"Bybit returned an invalid {propertyName}.");
        return value;
    }
}

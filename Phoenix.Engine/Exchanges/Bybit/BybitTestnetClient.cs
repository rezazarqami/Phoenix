using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Phoenix.Engine.Exchanges.Bybit;

public sealed partial class BybitDemoClient
{
    private readonly HttpClient _httpClient;
    private readonly BybitDemoOptions _options;
    private readonly Func<long> _timestampProvider;

    public BybitDemoClient(
        BybitDemoOptions options,
        HttpClient? httpClient = null,
        Func<long>? timestampProvider = null)
    {
        _options = options;
        _httpClient = httpClient ?? new HttpClient();
        _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _httpClient.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
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

    public async Task<IReadOnlyList<BybitKline>> GetKlinesAsync(
        string symbol,
        string interval,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        symbol = NormalizeSymbol(symbol);
        var allowedIntervals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "1", "3", "5", "15", "30", "60", "120", "240", "360", "720", "D", "W", "M"
        };
        interval = interval.Trim().ToUpperInvariant();
        if (!allowedIntervals.Contains(interval))
            throw new ArgumentException("Bybit candle interval is not supported.", nameof(interval));
        if (limit is < 50 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Candle limit must be between 50 and 1000.");

        var path = $"/v5/market/kline?category=linear&symbol={Uri.EscapeDataString(symbol)}" +
                   $"&interval={Uri.EscapeDataString(interval)}&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);

        var result = new List<BybitKline>();
        foreach (var item in document.RootElement.GetProperty("result").GetProperty("list").EnumerateArray())
        {
            if (!long.TryParse(item[0].GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var openTime))
                continue;
            result.Add(new BybitKline(
                openTime,
                ParseDecimal(item[1]),
                ParseDecimal(item[2]),
                ParseDecimal(item[3]),
                ParseDecimal(item[4]),
                ParseDecimal(item[5])));
        }
        result.Reverse();
        return result;
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
        var leverageFilter = item.GetProperty("leverageFilter");

        return new BybitInstrumentRules(
            item.GetProperty("symbol").GetString() ?? symbol,
            ReadDecimal(priceFilter, "tickSize"),
            ReadDecimal(lotFilter, "qtyStep"),
            ReadDecimal(lotFilter, "minOrderQty"),
            ReadDecimal(lotFilter, "minNotionalValue"),
            ReadDecimal(leverageFilter, "maxLeverage"),
            ReadDecimal(leverageFilter, "minLeverage"),
            ReadDecimal(leverageFilter, "leverageStep"));
    }

    public async Task<IReadOnlyList<string>> GetTradableLinearSymbolsAsync(
        CancellationToken cancellationToken = default)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;

        do
        {
            var path = "/v5/market/instruments-info?category=linear&limit=1000";
            if (!string.IsNullOrWhiteSpace(cursor))
                path += $"&cursor={Uri.EscapeDataString(cursor)}";

            using var response = await _httpClient.GetAsync(path, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(json);
            EnsureSuccess(document.RootElement);
            var result = document.RootElement.GetProperty("result");
            foreach (var item in result.GetProperty("list").EnumerateArray())
            {
                var status = item.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString()
                    : null;
                var symbol = item.TryGetProperty("symbol", out var symbolElement)
                    ? symbolElement.GetString()
                    : null;
                if (status == "Trading" && !string.IsNullOrWhiteSpace(symbol))
                    symbols.Add(symbol);
            }

            cursor = result.TryGetProperty("nextPageCursor", out var cursorElement)
                ? cursorElement.GetString()
                : null;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return symbols.OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<BybitDemoStatus> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var publicResponse = await _httpClient.GetAsync("/v5/market/time", cancellationToken);
        var publicJson = await publicResponse.Content.ReadAsStringAsync(cancellationToken);
        publicResponse.EnsureSuccessStatusCode();
        using (var publicDocument = JsonDocument.Parse(publicJson))
            EnsureSuccess(publicDocument.RootElement);

        if (!_options.HasCredentials)
            return new BybitDemoStatus(true, false, null,
                $"Public API connected; {_options.EnvironmentName} API keys are not configured.");

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

        return new BybitDemoStatus(true, true, equity,
            $"Bybit {_options.EnvironmentName} authentication succeeded.");
    }

    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken cancellationToken = default)
    {
        const string query = "accountType=UNIFIED&coin=USDT";
        using var request = CreateSignedGetRequest("/v5/account/wallet-balance", query);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        var account = document.RootElement.GetProperty("result").GetProperty("list")[0];
        var available = account.TryGetProperty("totalAvailableBalance", out var value)
            ? value.GetString() : null;
        if (!decimal.TryParse(available, NumberStyles.Number, CultureInfo.InvariantCulture, out var balance))
            throw new InvalidOperationException("Bybit returned an invalid available balance.");
        return balance;
    }

    public async Task<decimal> GetUsdtWalletBalanceAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateSignedGetRequest("/v5/account/wallet-balance", "accountType=UNIFIED&coin=USDT");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureSuccess(document.RootElement);
        var coins = document.RootElement.GetProperty("result").GetProperty("list")[0].GetProperty("coin");
        foreach (var coin in coins.EnumerateArray())
            if (coin.GetProperty("coin").GetString() == "USDT" &&
                decimal.TryParse(coin.GetProperty("walletBalance").GetString(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var balance)) return balance;
        throw new InvalidOperationException("Bybit did not return a valid USDT wallet balance.");
    }

    public async Task<decimal?> GetLeverageAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var query = $"category=linear&symbol={Uri.EscapeDataString(NormalizeSymbol(symbol))}";
        using var request = CreateSignedGetRequest("/v5/position/list", query);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        var positions = document.RootElement.GetProperty("result").GetProperty("list");
        foreach (var position in positions.EnumerateArray())
        {
            var leverage = TryReadDecimal(position, "leverage");
            if (leverage is > 0m)
                return leverage;
        }
        return null;
    }

    public async Task<int> GetPositionIndexAsync(
        string symbol,
        string side,
        CancellationToken cancellationToken = default)
    {
        var query = $"category=linear&symbol={Uri.EscapeDataString(NormalizeSymbol(symbol))}";
        using var request = CreateSignedGetRequest("/v5/position/list", query);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        var positions = document.RootElement.GetProperty("result").GetProperty("list");
        var hedgeMode = positions.EnumerateArray().Any(position =>
            position.TryGetProperty("positionIdx", out var index) && index.GetInt32() is 1 or 2);
        if (!hedgeMode) return 0;
        return string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    public async Task SetLeverageAsync(
        string symbol,
        decimal leverage,
        CancellationToken cancellationToken = default)
    {
        if (leverage <= 0m)
            throw new ArgumentOutOfRangeException(nameof(leverage));
        var value = FormatDecimal(leverage);
        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["category"] = "linear",
            ["symbol"] = NormalizeSymbol(symbol),
            ["buyLeverage"] = value,
            ["sellLeverage"] = value
        });
        using var request = CreateSignedPostRequest("/v5/position/set-leverage", body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        var code = document.RootElement.GetProperty("retCode").GetInt32();
        if (code != 0 && code != 110043)
            EnsureSuccess(document.RootElement);
    }

    public HttpRequestMessage CreateSignedGetRequest(string path, string queryString)
    {
        if (!_options.HasCredentials)
            throw new InvalidOperationException($"Bybit {_options.EnvironmentName} API credentials are not configured.");

        if (!path.StartsWith("/v5/", StringComparison.Ordinal))
            throw new InvalidOperationException("Only Bybit V5 endpoints are allowed.");

        var timestamp = _timestampProvider().ToString(CultureInfo.InvariantCulture);
        var receiveWindow = BybitDemoOptions.ReceiveWindowMilliseconds.ToString(CultureInfo.InvariantCulture);
        var payload = timestamp + _options.ApiKey + receiveWindow + queryString;
        var signature = BybitSignature.CreateHmacSha256(_options.ApiSecret!, payload);
        var request = new HttpRequestMessage(HttpMethod.Get, $"{path}?{queryString}");
        request.Headers.Add("X-BAPI-API-KEY", _options.ApiKey);
        request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
        request.Headers.Add("X-BAPI-RECV-WINDOW", receiveWindow);
        request.Headers.Add("X-BAPI-SIGN", signature);
        return request;
    }

    public async Task<BybitOrderResult> PlaceLimitOrderAsync(
        BybitOrderPreview preview,
        string? orderLinkId = null,
        int positionIndex = 0,
        CancellationToken cancellationToken = default)
    {
        orderLinkId ??= $"phoenix-{Guid.NewGuid():N}"[..36];
        if (orderLinkId.Length > 36 || orderLinkId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Order link ID is invalid.", nameof(orderLinkId));
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["category"] = "linear",
            ["symbol"] = NormalizeSymbol(preview.Symbol),
            ["side"] = preview.Side,
            ["orderType"] = "Limit",
            ["qty"] = FormatDecimal(preview.Quantity),
            ["price"] = FormatDecimal(preview.Price),
            ["timeInForce"] = "GTC",
            ["positionIdx"] = positionIndex,
            ["orderLinkId"] = orderLinkId,
            ["reduceOnly"] = false,
            ["takeProfit"] = FormatDecimal(preview.TakeProfit),
            ["stopLoss"] = FormatDecimal(preview.StopLoss),
            ["tpTriggerBy"] = "MarkPrice",
            ["slTriggerBy"] = "MarkPrice",
            ["tpslMode"] = "Full",
            ["tpOrderType"] = "Market",
            ["slOrderType"] = "Market"
        });

        using var request = CreateSignedPostRequest("/v5/order/create", body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        var result = document.RootElement.GetProperty("result");

        return new BybitOrderResult(
            result.GetProperty("orderId").GetString() ?? string.Empty,
            result.GetProperty("orderLinkId").GetString() ?? orderLinkId,
            preview.Symbol,
            preview.Side,
            preview.Quantity,
            preview.Price);
    }

    public async Task<BybitCancelResult> CancelOrderAsync(
        string symbol,
        string orderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            throw new ArgumentException("Order ID is required.", nameof(orderId));

        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["category"] = "linear",
            ["symbol"] = NormalizeSymbol(symbol),
            ["orderId"] = orderId
        });

        using var request = CreateSignedPostRequest("/v5/order/cancel", body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        var result = document.RootElement.GetProperty("result");
        return new BybitCancelResult(
            result.GetProperty("orderId").GetString() ?? orderId,
            result.GetProperty("orderLinkId").GetString() ?? string.Empty);
    }

    public async Task<BybitOrderResult> PlaceStopLimitAsync(
        string symbol, string direction, decimal quantity, decimal stopPrice,
        decimal tickSize, string orderLinkId, int positionIndex = 0,
        CancellationToken cancellationToken = default)
    {
        var price = BybitOrderPreviewBuilder.RoundToStep(stopPrice, tickSize);
        var isLong = string.Equals(direction, "Long", StringComparison.OrdinalIgnoreCase);
        var side = isLong ? "Sell" : "Buy";
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["category"] = "linear",
            ["symbol"] = NormalizeSymbol(symbol),
            ["side"] = side,
            ["orderType"] = "Limit",
            ["qty"] = FormatDecimal(quantity),
            ["price"] = FormatDecimal(price),
            ["triggerPrice"] = FormatDecimal(price),
            ["triggerDirection"] = isLong ? 2 : 1,
            ["triggerBy"] = "MarkPrice",
            ["timeInForce"] = "GTC",
            ["positionIdx"] = positionIndex,
            ["orderLinkId"] = orderLinkId,
            ["reduceOnly"] = true,
            ["closeOnTrigger"] = true
        });
        using var request = CreateSignedPostRequest("/v5/order/create", body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        var result = document.RootElement.GetProperty("result");
        return new BybitOrderResult(
            result.GetProperty("orderId").GetString() ?? string.Empty,
            result.GetProperty("orderLinkId").GetString() ?? orderLinkId,
            symbol, side, quantity, price);
    }

    public async Task<BybitOrderStatus?> GetOrderStatusAsync(
        string orderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            throw new ArgumentException("Order ID is required.", nameof(orderId));

        var query = $"category=linear&orderId={Uri.EscapeDataString(orderId)}";
        using var request = CreateSignedGetRequest("/v5/order/realtime", query);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        var list = document.RootElement.GetProperty("result").GetProperty("list");
        if (list.GetArrayLength() == 0) return null;

        var item = list[0];
        var averagePrice = TryReadDecimal(item, "avgPrice");
        var executedQuantity = TryReadDecimal(item, "cumExecQty") ?? 0m;
        DateTime? updatedAtUtc = null;
        var updatedText = item.TryGetProperty("updatedTime", out var updated) ? updated.GetString() : null;
        if (long.TryParse(updatedText, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
            updatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;

        return new BybitOrderStatus(
            item.GetProperty("orderId").GetString() ?? orderId,
            item.GetProperty("symbol").GetString() ?? string.Empty,
            item.GetProperty("orderStatus").GetString() ?? "Unknown",
            averagePrice,
            executedQuantity,
            updatedAtUtc);
    }

    public async Task<BybitOrderStatus?> GetOrderStatusByLinkIdAsync(
        string orderLinkId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderLinkId))
            throw new ArgumentException("Order link ID is required.", nameof(orderLinkId));
        var query = $"category=linear&orderLinkId={Uri.EscapeDataString(orderLinkId)}";
        using var request = CreateSignedGetRequest("/v5/order/realtime", query);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        EnsureSuccess(document.RootElement);
        var list = document.RootElement.GetProperty("result").GetProperty("list");
        if (list.GetArrayLength() == 0) return null;
        return ParseOrderStatus(list[0], orderLinkId);
    }

    public HttpRequestMessage CreateSignedPostRequest(string path, string jsonBody)
    {
        if (!_options.HasCredentials)
            throw new InvalidOperationException($"Bybit {_options.EnvironmentName} API credentials are not configured.");
        if (!path.StartsWith("/v5/", StringComparison.Ordinal))
            throw new InvalidOperationException("Only Bybit V5 endpoints are allowed.");

        var timestamp = _timestampProvider().ToString(CultureInfo.InvariantCulture);
        var receiveWindow = BybitDemoOptions.ReceiveWindowMilliseconds.ToString(CultureInfo.InvariantCulture);
        var signature = BybitSignature.CreateHmacSha256(
            _options.ApiSecret!, timestamp + _options.ApiKey + receiveWindow + jsonBody);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-BAPI-API-KEY", _options.ApiKey);
        request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
        request.Headers.Add("X-BAPI-RECV-WINDOW", receiveWindow);
        request.Headers.Add("X-BAPI-SIGN", signature);
        return request;
    }

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol is required.", nameof(symbol));

        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Symbol can only contain ASCII letters, digits, and hyphens.", nameof(symbol));
        return normalized;
    }

    private static decimal ParseDecimal(JsonElement element)
    {
        if (decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new InvalidOperationException("Bybit returned an invalid candle value.");
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

    private static decimal? TryReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return null;
        var text = property.GetString();
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static BybitOrderStatus ParseOrderStatus(JsonElement item, string fallbackId)
    {
        var averagePrice = TryReadDecimal(item, "avgPrice");
        var executedQuantity = TryReadDecimal(item, "cumExecQty") ?? 0m;
        DateTime? updatedAtUtc = null;
        var updatedText = item.TryGetProperty("updatedTime", out var updated) ? updated.GetString() : null;
        if (long.TryParse(updatedText, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
            updatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        return new BybitOrderStatus(
            item.GetProperty("orderId").GetString() ?? fallbackId,
            item.GetProperty("symbol").GetString() ?? string.Empty,
            item.GetProperty("orderStatus").GetString() ?? "Unknown",
            averagePrice, executedQuantity, updatedAtUtc);
    }
}

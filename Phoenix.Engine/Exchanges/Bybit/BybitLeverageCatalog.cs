using System.Text.Json;

namespace Phoenix.Engine.Exchanges.Bybit;

public sealed partial class BybitDemoClient
{
    // Informational bulk snapshot only. Execution still fetches instrument rules independently.
    public async Task<IReadOnlyDictionary<string, BybitInstrumentRules>> GetLinearLeverageCatalogAsync(
        CancellationToken token = default)
    {
        var rules = new Dictionary<string, BybitInstrumentRules>(StringComparer.OrdinalIgnoreCase);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var path = "/v5/market/instruments-info?category=linear&limit=1000";
            if (!string.IsNullOrWhiteSpace(cursor)) path += "&cursor=" + Uri.EscapeDataString(cursor);
            using var response = await _httpClient.GetAsync(path, token);
            var json = await response.Content.ReadAsStringAsync(token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(json);
            EnsureSuccess(document.RootElement);
            var result = document.RootElement.GetProperty("result");
            foreach (var item in result.GetProperty("list").EnumerateArray())
            {
                if (item.GetProperty("status").GetString() != "Trading") continue;
                var symbol = item.GetProperty("symbol").GetString();
                if (string.IsNullOrWhiteSpace(symbol) || !item.TryGetProperty("leverageFilter", out var leverage)) continue;
                var maximum = TryReadDecimal(leverage, "maxLeverage");
                var minimum = TryReadDecimal(leverage, "minLeverage");
                var step = TryReadDecimal(leverage, "leverageStep");
                if (maximum is not > 0 || minimum is not > 0 || step is not > 0 || minimum > maximum) continue;
                // Price and quantity filters are intentionally unused in this display-only catalog.
                rules[symbol] = new(symbol, 0, 0, 0, 0, maximum.Value, minimum.Value, step.Value);
            }
            cursor = result.TryGetProperty("nextPageCursor", out var next) ? next.GetString() : null;
            if (!string.IsNullOrWhiteSpace(cursor) && !cursors.Add(cursor))
                throw new InvalidDataException("Bybit instrument pagination repeated a cursor.");
        } while (!string.IsNullOrWhiteSpace(cursor));
        if (rules.Count == 0) throw new InvalidDataException("Bybit returned no valid leverage limits.");
        return rules;
    }
}

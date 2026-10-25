using System.Text.Json;

namespace Phoenix.Engine.Exchanges.Bybit;

public sealed record BybitOpenPosition(string Symbol, string Side, int PositionIndex, decimal Size);

public sealed partial class BybitDemoClient
{
    public async Task<IReadOnlyList<BybitOpenPosition>> GetOpenPositionsAsync(CancellationToken token = default)
    {
        var result = new List<BybitOpenPosition>();
        string? cursor = null;
        do
        {
            var query = "category=linear&settleCoin=USDT&limit=200";
            if (!string.IsNullOrEmpty(cursor)) query += "&cursor=" + Uri.EscapeDataString(cursor);
            using var request = CreateSignedGetRequest("/v5/position/list", query);
            using var response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            EnsureSuccess(document.RootElement);
            var data = document.RootElement.GetProperty("result");
            foreach (var p in data.GetProperty("list").EnumerateArray())
            {
                var size = TryReadDecimal(p, "size") ?? 0m;
                var side = p.GetProperty("side").GetString();
                if (size > 0m && side is "Buy" or "Sell")
                    result.Add(new(p.GetProperty("symbol").GetString()!, side,
                        p.GetProperty("positionIdx").GetInt32(), size));
            }
            cursor = data.TryGetProperty("nextPageCursor", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(cursor));
        return result;
    }

    public async Task<string> ClosePositionAsync(BybitOpenPosition position, CancellationToken token = default)
    {
        if (position.Size <= 0 || position.Side is not ("Buy" or "Sell") ||
            position.PositionIndex is < 0 or > 2) throw new ArgumentException("Invalid position.");
        var body = JsonSerializer.Serialize(new
        {
            category = "linear", symbol = NormalizeSymbol(position.Symbol),
            side = position.Side == "Buy" ? "Sell" : "Buy",
            orderType = "Market", qty = FormatDecimal(position.Size),
            positionIdx = position.PositionIndex, reduceOnly = true,
            orderLinkId = $"close-{Guid.NewGuid():N}"[..36]
        });
        using var request = CreateSignedPostRequest("/v5/order/create", body);
        using var response = await _httpClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        EnsureSuccess(document.RootElement);
        return document.RootElement.GetProperty("result").GetProperty("orderId").GetString()!;
    }
}

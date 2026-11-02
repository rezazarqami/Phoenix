using System.Text.Json;

namespace Phoenix.Engine.Exchanges.Bybit;

public sealed partial class BybitDemoClient
{
    // Unlike an individual historical lookup, this proves whether orders remain live.
    public async Task<IReadOnlyList<BybitOrderStatus>> GetOpenLinearOrdersAsync(CancellationToken token = default)
    {
        var orders = new List<BybitOrderStatus>();
        var cursors = new HashSet<string>();
        string? cursor = null;
        do
        {
            var query = "category=linear&settleCoin=USDT&openOnly=0&limit=50";
            if (!string.IsNullOrEmpty(cursor)) query += "&cursor=" + Uri.EscapeDataString(cursor);
            using var request = CreateSignedGetRequest("/v5/order/realtime", query);
            using var response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            EnsureSuccess(document.RootElement);
            var result = document.RootElement.GetProperty("result");
            foreach (var item in result.GetProperty("list").EnumerateArray())
                orders.Add(ParseOrderStatus(item, ""));
            cursor = result.TryGetProperty("nextPageCursor", out var next) ? next.GetString() : null;
            if (!string.IsNullOrEmpty(cursor) && !cursors.Add(cursor))
                throw new InvalidDataException("Repeated Bybit open-order cursor; recovery is unconfirmed.");
        } while (!string.IsNullOrEmpty(cursor));
        return orders;
    }
}

using System.Globalization;
using System.Text.Json;
namespace Phoenix.Engine.Exchanges.Bybit;

public sealed record BybitCashTransaction(string Id,string OrderId,string Symbol,string Side,string Type,
    DateTime At,decimal? Quantity,decimal? RemainingSize,decimal? CashBalance);
public sealed record BybitClosedTrade(string OrderId,string Symbol,string Side,DateTime At,
    decimal? EntryPrice,decimal? ClosedSize);

public sealed partial class BybitDemoClient
{
    public async Task<IReadOnlyList<BybitCashTransaction>> GetCashTransactionsAsync(DateTime from,DateTime to,CancellationToken ct=default)
    {
        var rows=await ReadWalletHistoryPagesAsync("/v5/account/transaction-log","accountType=UNIFIED&category=linear&currency=USDT&limit=50",from,to,ct);
        return rows.Where(p=>ReadText(p,"currency")=="USDT"&&ReadText(p,"category")=="linear")
            .Select(p=>new BybitCashTransaction(ReadText(p,"id"),ReadText(p,"orderId"),ReadText(p,"symbol"),ReadText(p,"side"),ReadText(p,"type"),ReadTime(p,"transactionTime"),TryReadDecimal(p,"qty"),TryReadDecimal(p,"size"),TryReadDecimal(p,"cashBalance")))
            .Where(p=>!string.IsNullOrWhiteSpace(p.Id)).Distinct().ToArray();
    }
    public async Task<IReadOnlyList<BybitClosedTrade>> GetClosedTradesAsync(DateTime from,DateTime to,CancellationToken ct=default)
    {
        var rows=await ReadWalletHistoryPagesAsync("/v5/position/closed-pnl","category=linear&limit=100",from,to,ct);
        return rows.Where(p=>ReadText(p,"execType") is "Trade" or "BustTrade")
            .Select(p=>new BybitClosedTrade(ReadText(p,"orderId"),ReadText(p,"symbol"),ReadText(p,"side"),ReadTime(p,"updatedTime"),TryReadDecimal(p,"avgEntryPrice"),TryReadDecimal(p,"closedSize")))
            .Where(p=>!string.IsNullOrWhiteSpace(p.OrderId)).Distinct().ToArray();
    }
    private async Task<List<JsonElement>> ReadWalletHistoryPagesAsync(string path,string filter,DateTime from,DateTime to,CancellationToken ct)
    {
        if(from.Kind!=DateTimeKind.Utc||to.Kind!=DateTimeKind.Utc||from>to||to-from>TimeSpan.FromDays(7))throw new ArgumentException("History window must be UTC and at most seven days.");
        var query=filter+"&startTime="+new DateTimeOffset(from).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)+"&endTime="+new DateTimeOffset(to).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var rows=new List<JsonElement>();var seen=new HashSet<string>();string? cursor=null;
        for(var page=0;page<200;page++)
        {
            using var request=CreateSignedGetRequest(path,query+(cursor is null?"":"&cursor="+Uri.EscapeDataString(cursor)));
            using var response=await _httpClient.SendAsync(request,ct);response.EnsureSuccessStatusCode();
            using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));EnsureSuccess(document.RootElement);
            var result=document.RootElement.GetProperty("result");rows.AddRange(result.GetProperty("list").EnumerateArray().Select(p=>p.Clone()));
            cursor=result.TryGetProperty("nextPageCursor",out var next)?next.GetString():null;
            if(string.IsNullOrEmpty(cursor))return rows;
            if(!seen.Add(cursor))throw new InvalidOperationException("Bybit repeated a history cursor; history is incomplete.");
        }
        throw new InvalidOperationException("Bybit history exceeded the page limit; no partial history will be used.");
    }
    private static string ReadText(JsonElement p,string field)=>p.TryGetProperty(field,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    private static DateTime ReadTime(JsonElement p,string field)=>long.TryParse(ReadText(p,field),out var at)&&at is >=0 and <=253402300799999
        ?DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime:DateTime.MinValue;
}

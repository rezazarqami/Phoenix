using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phoenix.Web;
public sealed record TelegramWalletImportRequest(JsonElement Export);
public sealed record TelegramWalletImportResult(int Imported,int Unresolved,int Examined);

public sealed partial class MobileSignalFeed
{
    private sealed record WalletMessage(long Id,DateTime At,string Kind,string Symbol,string Direction,
        decimal Entry,decimal Target,decimal Stop,decimal Balance);

    // An owner-supplied Telegram Desktop export is the source, never a current balance or a guessed PnL.
    public async Task<TelegramWalletImportResult> ImportWalletsAsync(string owner,JsonElement export,CancellationToken ct)
    {
        if(export.ValueKind!=JsonValueKind.Object || !export.TryGetProperty("messages",out var messages) || messages.ValueKind!=JsonValueKind.Array)
            throw new ArgumentException("خروجی JSON یک گفت‌وگوی تلگرام را انتخاب کن.");
        var raw=export.GetRawText();if(Encoding.UTF8.GetByteCount(raw)>2_097_152)throw new ArgumentException("فایل خروجی باید حداکثر ۲ مگابایت باشد.");
        var parsed=messages.EnumerateArray().Select(ParseWalletMessage).OfType<WalletMessage>().ToArray();
        // Initialize the journal, including archived results, before selecting the latest 50 final outcomes.
        await HistoryAsync(owner,null,null,ct,"results");await gate.WaitAsync(ct);
        try
        {
            await using var db=await OpenAsync(ct);
            var rows=new List<DeviceSignalAlert>();
            using(var cmd=db.CreateCommand())
            {
                cmd.CommandText="SELECT payload FROM device_signal_history WHERE owner=$o AND json_extract(payload,'$.Kind') IN ('Target','RiskFree','StopLoss') ORDER BY at_utc DESC,seq DESC LIMIT 50";
                cmd.Parameters.AddWithValue("$o",owner.ToLowerInvariant());
                using var reader=await cmd.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))if(JsonSerializer.Deserialize<DeviceSignalAlert>(reader.GetString(0)) is {} e)rows.Add(e);
            }
            using var tx=db.BeginTransaction();
            using(var schema=db.CreateCommand())
            {
                schema.Transaction=tx;schema.CommandText="CREATE TABLE IF NOT EXISTS device_wallet_imports(owner TEXT NOT NULL,event_id TEXT NOT NULL,message_id INTEGER NOT NULL,message_at TEXT NOT NULL,export_sha256 TEXT NOT NULL,imported_at TEXT NOT NULL,PRIMARY KEY(owner,event_id))";
                await schema.ExecuteNonQueryAsync(ct);
            }
            var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));int imported=0,unresolved=0;
            foreach(var e in rows.Where(e=>e.WalletBalanceUsdt is null))
            {
                var matches=parsed.Where(m=>Matches(e,m)).ToArray();
                // Do not choose between ambiguous trades or conflicting balances.
                if(matches.Length==0 || matches.Select(m=>m.Balance).Distinct().Count()!=1 ||
                    matches.Any(m=>rows.Count(other=>Matches(other,m))!=1)){unresolved++;continue;}
                var message=matches.OrderBy(m=>m.At).First();
                using var update=db.CreateCommand();update.Transaction=tx;
                update.CommandText="UPDATE device_signal_history SET payload=$p WHERE owner=$o AND event_id=$id AND json_extract(payload,'$.WalletBalanceUsdt') IS NULL";
                update.Parameters.AddWithValue("$p",JsonSerializer.Serialize(e with{WalletBalanceUsdt=message.Balance}));
                update.Parameters.AddWithValue("$o",owner.ToLowerInvariant());update.Parameters.AddWithValue("$id",e.Id);
                if(await update.ExecuteNonQueryAsync(ct)==0)continue;
                using var audit=db.CreateCommand();audit.Transaction=tx;
                audit.CommandText="INSERT OR IGNORE INTO device_wallet_imports VALUES($o,$id,$m,$at,$hash,$now)";
                audit.Parameters.AddWithValue("$o",owner.ToLowerInvariant());audit.Parameters.AddWithValue("$id",e.Id);audit.Parameters.AddWithValue("$m",message.Id);
                audit.Parameters.AddWithValue("$at",message.At.ToString("O"));audit.Parameters.AddWithValue("$hash",hash);audit.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
                await audit.ExecuteNonQueryAsync(ct);imported++;
            }
            tx.Commit();return new(imported,unresolved,rows.Count);
        }
        finally{gate.Release();}
    }
    private static bool Matches(DeviceSignalAlert e,WalletMessage m)=>e.Kind==m.Kind &&
        e.Symbol.Equals(m.Symbol,StringComparison.OrdinalIgnoreCase) && e.Direction.Equals(m.Direction,StringComparison.OrdinalIgnoreCase) &&
        e.EntryPrice==m.Entry && e.TakeProfit==m.Target && e.StopLoss==m.Stop &&
        m.At>=e.At.AddSeconds(-5) && m.At<=e.At.AddDays(1);

    private static WalletMessage? ParseWalletMessage(JsonElement message)
    {
        if(message.ValueKind!=JsonValueKind.Object || !message.TryGetProperty("text",out var body))return null;
        var text=NormalizeMessage(ExportText(body));var heading=text.Split('\n')[0];
        var kind=heading.Contains("تارگت")&&heading.Contains("رسید")?"Target":heading.Contains("استاپ")&&heading.Contains("رسید")?"StopLoss":
            heading.Contains("ریسک")&&heading.Contains("بسته")?"RiskFree":null;
        if(kind is null)return null;
        string Field(string name)=>Regex.Match(text,@"(?:^|\n)\s*"+name+@"\s*:\s*([^\n]+)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)).Groups[1].Value.Trim();
        decimal? Price(string name)=>decimal.TryParse(Regex.Match(Field(name),@"^[0-9][0-9.,]*").Value,NumberStyles.Number,CultureInfo.InvariantCulture,out var n)?n:null;
        var wallet=Regex.Match(text,@"موجودی\s+کیف\s+پول(?:\s+USDT)?(?:\s+در\s+زمان\s+اعلان)?\s*:\s*([0-9][0-9.,]*)\s*USDT",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
        if(!decimal.TryParse(wallet.Groups[1].Value,NumberStyles.Number,CultureInfo.InvariantCulture,out var balance))return null;
        var symbol=Field("نماد");var direction=Field("جهت");var entry=Price("ورود");var target=Price("تارگت");var stop=Price("استاپ");
        if(!Regex.IsMatch(symbol,@"\A[A-Z0-9]{3,30}\z") || direction is not ("Long" or "Short") || entry is null || target is null || stop is null)return null;
        if(!message.TryGetProperty("date_unixtime",out var time) || !long.TryParse(time.ToString(),out var epoch) || epoch is <0 or >253402300799)return null;
        var id=message.TryGetProperty("id",out var identifier)&&identifier.ValueKind==JsonValueKind.Number&&identifier.TryGetInt64(out var number)?number:0;
        return new(id,DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime,kind,symbol,direction,entry.Value,target.Value,stop.Value,balance);
    }
    private static string ExportText(JsonElement e)=>e.ValueKind switch
    {
        JsonValueKind.String=>e.GetString()??"",
        JsonValueKind.Array=>string.Concat(e.EnumerateArray().Select(ExportText)),
        JsonValueKind.Object when e.TryGetProperty("text",out var t)=>ExportText(t),_=>""
    };
    private static string NormalizeMessage(string text)
    {
        var output=new StringBuilder();foreach(var c in text)
        {
            var index="۰۱۲۳۴۵۶۷۸۹".IndexOf(c);if(index<0)index="٠١٢٣٤٥٦٧٨٩".IndexOf(c);
            if(index>=0)output.Append((char)('0'+index));else if(c=='٫')output.Append('.');else if(c=='٬')output.Append(',');
            else if(c is not ('\u200c' or '\u200e' or '\u200f' or '\r'))output.Append(c);
        }
        return output.ToString();
    }
}

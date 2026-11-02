using System.Globalization;
using System.Text.Json;

namespace Phoenix.Web;

// Deliberately no exchange-account, order-store, strategy or execution-service dependencies.
public sealed class MarketRiskObserver(IHttpClientFactory clients, ILogger<MarketRiskObserver> log) : BackgroundService
{
    public sealed class Archive
    {
        public List<RiskSample> Samples { get; set; } = [];
        public List<RiskAlert> Alerts { get; set; } = [];
        public List<RiskEvent> Events { get; set; } = [];
        public List<RiskSource> Sources { get; set; } = [];
        public Dictionary<string, DateTimeOffset> Notices { get; set; } = [];
    }
    readonly object gate = new();
    Archive archive = new();
    RiskMetric[] metrics = [];
    readonly string path = Environment.GetEnvironmentVariable("PHOENIX_MARKET_RISK_PATH") ?? Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")) ?? Path.Combine(AppContext.BaseDirectory, "data"), "market-risk.json");
    DateTimeOffset nextCalendar, nextSave, lastDanger;
    bool armed = true;
    int calmTicks;
    static readonly string[] Symbols = ["BTCUSDT", "ETHUSDT"];
    static readonly (string Name, string Url)[] Feeds = [
        ("BLS", "https://www.bls.gov/schedule/news_release/bls.ics"),
        ("BEA", "https://www.bea.gov/news/schedule/ics/online-calendar-subscription.ics"),
        ("FED", "https://www.federalreserve.gov/json/calendar.json")];
    public RiskSnapshot Snapshot()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var fresh = metrics.Select(m => m.At is null ? m with { State = "missing", Score = null } : now - m.At > TimeSpan.FromSeconds(m.Key.EndsWith("-volume") ? 100 : 45) ? m with { State = "stale", Score = null } : m).ToArray();
            var overall = MarketRiskMath.Overall(fresh);
            return new(now, overall.Score, overall.State, fresh, archive.Events.Where(e => e.At >= now.AddHours(-2)).OrderBy(e => e.At).Take(100).ToArray(),
                Feeds.Select(f => { var s = archive.Sources.FirstOrDefault(s => s.Name == f.Name) ?? new(f.Name, null, "missing"); return s.UpdatedAt < now.AddHours(-12) ? s with { State = "stale" } : s; }).ToArray(),
                archive.Alerts.OrderByDescending(a => a.At).Take(60).ToArray(), archive.Samples.Count);
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (File.Exists(path)) archive = JsonSerializer.Deserialize<Archive>(await File.ReadAllTextAsync(path, stoppingToken)) ?? new();
            archive.Samples = archive.Samples.Where(s => s.At > DateTimeOffset.UtcNow.AddDays(-7) && double.IsFinite(s.Value) && s.Value >= 0).TakeLast(150000).ToList();
            lastDanger = archive.Alerts.Where(a => a.Kind == "market").Select(a => a.At).DefaultIfEmpty().Max();
        }
        catch (Exception ex) { log.LogWarning(ex, "Risk observer history unavailable; starting calibration"); }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Calendar and public prices run independently; calendar failures never delay market polling.
                var calendar = DateTimeOffset.UtcNow >= nextCalendar ? RefreshCalendar(stoppingToken) : Task.CompletedTask;
                await PollMarket(stoppingToken);
                await calendar;
                lock (gate) CheckCalendar(DateTimeOffset.UtcNow);
                if (DateTimeOffset.UtcNow >= nextSave) { await Save(stoppingToken); nextSave = DateTimeOffset.UtcNow.AddMinutes(1); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Independent risk observer cycle failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { break; }
        }
        try { await Save(CancellationToken.None); } catch (Exception ex) { log.LogWarning(ex, "Risk observer history save failed"); }
    }
    async Task<JsonDocument> Public(string endpoint, CancellationToken token)
    {
        using var response = await clients.CreateClient("MarketRisk").GetAsync("https://api.bybit.com/v5/market/" + endpoint, token);
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (doc.RootElement.GetProperty("retCode").GetInt32() != 0) { doc.Dispose(); throw new InvalidDataException("Public market API rejected request"); }
        return doc;
    }
    static double Num(JsonElement e) => double.Parse(e.GetString()!, CultureInfo.InvariantCulture);
    async Task PollMarket(CancellationToken token)
    {
        var data = await Task.WhenAll(Symbols.SelectMany(s => new[]{ ReadCandles(s,token), ReadBook(s,token) }));
        lock (gate)
        {
            metrics = data.SelectMany(a => a).ToArray();
            var now = DateTimeOffset.UtcNow;
            archive.Samples.RemoveAll(s => s.At < now.AddDays(-7));
            if (archive.Samples.Count > 150000) archive.Samples = archive.Samples.TakeLast(150000).ToList();
            var overall = MarketRiskMath.Overall(metrics);
            if (overall.State == "danger" && armed && now - lastDanger > TimeSpan.FromMinutes(5))
            {
                var causes = string.Join("، ", metrics.Where(m => m.Score >= 70).Select(m => m.Label));
                archive.Alerts.Add(new(Guid.NewGuid().ToString("N"),now,"market","danger","ناهنجاری شدید در بازار",causes + "؛ فقط هشدار — معاملات بدون تغییر ادامه دارند."));
                lastDanger = now; armed = false; calmTicks = 0;
            }
            else if (overall.Score.HasValue && overall.Score < 45 && metrics.All(m => m.State == "live"))
            { if (++calmTicks >= 8) armed = true; }
            else calmTicks = 0;
            archive.Alerts.RemoveAll(a => a.At < now.AddDays(-30));
            archive.Alerts = archive.Alerts.TakeLast(1000).ToList();
        }
    }
    RiskMetric Make(string key, string label, string unit, double value, DateTimeOffset at, IEnumerable<double>? baseline = null, bool inverse = false, double floor = .000001)
    {
        lock (gate)
        {
            if (!double.IsFinite(value) || value < 0) throw new InvalidDataException("Invalid observation");
            var history = (baseline ?? archive.Samples.Where(s => s.Key == key && s.At < at && s.At >= at.AddHours(-6)).Select(s => s.Value)).Where(double.IsFinite).ToArray();
            var score = MarketRiskMath.Score(value, history, inverse, floor);
            // Exclude current observation from its baseline and deduplicate exchange timestamps.
            if (!archive.Samples.Any(s => s.Key == key && s.At == at)) archive.Samples.Add(new(key,at,value));
            return new(key,label,unit,value,history.Length == 0 ? null : MarketRiskMath.Median(history),score,history.Length,at,score.HasValue ? "live" : "calibrating");
        }
    }
    static RiskMetric Missing(string key,string label,string unit) => new(key,label,unit,null,null,null,0,null,"missing");
    async Task<RiskMetric[]> ReadCandles(string symbol, CancellationToken token)
    {
        var shortName = symbol[..3];
        try
        {
            using var doc = await Public($"kline?category=linear&symbol={symbol}&interval=1&limit=122",token);
            var result = doc.RootElement.GetProperty("result");
            if (result.GetProperty("symbol").GetString() != symbol) throw new InvalidDataException("Symbol mismatch");
            var rows = result.GetProperty("list").EnumerateArray().OrderByDescending(r => long.Parse(r[0].GetString()!,CultureInfo.InvariantCulture)).ToArray();
            var now = DateTimeOffset.UtcNow;
            if (rows.Length < 62) throw new InvalidDataException("Insufficient candle history");
            DateTimeOffset At(JsonElement row) => DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(row[0].GetString()!,CultureInfo.InvariantCulture));
            if (now - At(rows[0]) > TimeSpan.FromSeconds(90) || At(rows[0]) > now.AddSeconds(5)) throw new InvalidDataException("Stale candles");
            var closed = rows.Where(r => At(r).AddMinutes(1) <= now).ToArray();
            double Move(JsonElement r) { var open = Num(r[1]); return open > 0 ? Math.Abs(Num(r[4]) / open - 1) * 100 : throw new InvalidDataException("Invalid price"); }
            var volume = Num(closed[0][6]); // Turnover in USDT of a full, closed minute.
            var baseline = closed.Skip(1).Take(120).ToArray();
            if (baseline.Any(r => Num(r[6]) < 0) || volume < 0) throw new InvalidDataException("Invalid volume");
            return [ Make(symbol+"-move",shortName+" · حرکت یک‌دقیقه","%",Move(rows[0]),now,baseline.Select(Move),floor:.03),
                Make(symbol+"-volume",shortName+" · حجم معاملات","USDT",volume,At(closed[0]).AddMinutes(1),baseline.Select(r=>Num(r[6])),floor:1) ];
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogDebug(ex,"Risk candle source unavailable for {Symbol}",symbol); return [Missing(symbol+"-move",shortName+" · حرکت یک‌دقیقه","%"),Missing(symbol+"-volume",shortName+" · حجم معاملات","USDT")]; }
    }
    async Task<RiskMetric[]> ReadBook(string symbol,CancellationToken token)
    {
        var name = symbol[..3];
        try
        {
            using var doc = await Public($"orderbook?category=linear&symbol={symbol}&limit=50",token);
            var r = doc.RootElement.GetProperty("result");
            if (r.GetProperty("s").GetString() != symbol) throw new InvalidDataException("Symbol mismatch");
            var at = DateTimeOffset.FromUnixTimeMilliseconds(r.GetProperty("ts").GetInt64());
            if (DateTimeOffset.UtcNow-at > TimeSpan.FromSeconds(30) || at > DateTimeOffset.UtcNow.AddSeconds(5)) throw new InvalidDataException("Stale book");
            var bids = r.GetProperty("b").EnumerateArray().ToArray(); var asks = r.GetProperty("a").EnumerateArray().ToArray();
            var bid = Num(bids[0][0]); var ask = Num(asks[0][0]); var mid = (bid+ask)/2;
            if (bid <= 0 || ask < bid) throw new InvalidDataException("Invalid book");
            var depth = bids.Concat(asks).Where(l=>Math.Abs(Num(l[0])/mid-1)<=.005).Sum(l=>Num(l[0])*Num(l[1]));
            if (!double.IsFinite(depth) || depth <= 0) throw new InvalidDataException("Invalid depth");
            return [Make(symbol+"-spread",name+" · شکاف خریدوفروش","bp",(ask-bid)/mid*10000,at,floor:.02),Make(symbol+"-depth",name+" · افت عمق ۵۰ ردیف","USDT",depth,at,inverse:true,floor:1)];
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogDebug(ex,"Risk orderbook unavailable for {Symbol}",symbol); return [Missing(symbol+"-spread",name+" · شکاف خریدوفروش","bp"),Missing(symbol+"-depth",name+" · افت عمق ۵۰ ردیف","USDT")]; }
    }
    async Task RefreshCalendar(CancellationToken token)
    {
        var success = await Task.WhenAll(Feeds.Select(async f =>
        {
            try
            {
                var content = await clients.CreateClient("MarketRisk").GetStringAsync(f.Url,token);
                var events = f.Name == "FED" ? MarketRiskCalendar.ParseFed(content) : MarketRiskCalendar.ParseIcs(content,f.Name,f.Url);
                if (events.Length == 0) throw new InvalidDataException("Calendar returned no recognized timed events");
                lock (gate)
                {
                    archive.Events.RemoveAll(e=>e.Source==f.Name);
                    archive.Events.AddRange(events.Where(e=>e.At>DateTimeOffset.UtcNow.AddDays(-2)).DistinctBy(e=>e.Id));
                    archive.Sources.RemoveAll(s=>s.Name==f.Name);archive.Sources.Add(new(f.Name,DateTimeOffset.UtcNow,"live"));
                }
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.LogWarning(ex,"Risk calendar source {Source} unavailable",f.Name);
                lock (gate) { var old=archive.Sources.FirstOrDefault(s=>s.Name==f.Name);archive.Sources.RemoveAll(s=>s.Name==f.Name);archive.Sources.Add(new(f.Name,old?.UpdatedAt,"unavailable")); }
                return false;
            }
        }));
        nextCalendar = DateTimeOffset.UtcNow.Add(success.All(s=>s) ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(15));
    }
    void CheckCalendar(DateTimeOffset now)
    {
        foreach(var e in archive.Events.Where(e=>e.At>now&&e.At<=now.AddMinutes(60)))
        {
            var source = archive.Sources.FirstOrDefault(s=>s.Name==e.Source);
            if (source?.UpdatedAt is not { } updated || now-updated>TimeSpan.FromHours(12)) continue;
            var left=(e.At-now).TotalMinutes; var window=left<=5?5:left<=30?30:60;
            var key=e.Id+":"+e.At.ToString("O")+":"+window;
            if(archive.Notices.ContainsKey(key))continue;
            archive.Notices[key]=now;
            archive.Alerts.Add(new(Guid.NewGuid().ToString("N"),now,"calendar",window<=5?"danger":"warning","رویداد اقتصادی نزدیک است",e.Title+" · کمتر از "+window+" دقیقه؛ معاملات بدون تغییر ادامه دارند."));
        }
        foreach(var key in archive.Notices.Where(p=>p.Value<now.AddDays(-7)).Select(p=>p.Key).ToArray())archive.Notices.Remove(key);
    }
    async Task Save(CancellationToken token)
    {
        string json; lock(gate) json=JsonSerializer.Serialize(archive);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary=path+".tmp";
        await File.WriteAllTextAsync(temporary,json,token);
        File.Move(temporary,path,true);
    }
}

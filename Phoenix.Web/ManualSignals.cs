using Phoenix.Engine.Exchanges.Bybit;
namespace Phoenix.Web;

public sealed record ManualSignalRequest(Guid RequestId, string Symbol, string Direction, string Timeframe,
    decimal EntryPrice, decimal TakeProfit, decimal StopLoss, decimal ExpirePrice, decimal PositionSizeUsdt, decimal Leverage)
{
    public string? Validate()
    {
        if (RequestId == Guid.Empty) return "شناسه ثبت معتبر نیست.";
        if (string.IsNullOrWhiteSpace(Symbol) || Symbol.Length > 40 || Symbol.Any(c => !char.IsAsciiLetterOrDigit(c))) return "نماد معتبر نیست.";
        if (Direction is not ("Long" or "Short")) return "جهت معامله معتبر نیست.";
        if (Timeframe is not ("1" or "5" or "15" or "30" or "60" or "240" or "D" or "W" or "M" or "3M")) return "تایم‌فریم معتبر نیست.";
        if (new[] { EntryPrice, TakeProfit, StopLoss, ExpirePrice }.Any(x => x <= 0 || x > 1000000000000m)) return "قیمت خطوط باید مثبت و معتبر باشد.";
        if (PositionSizeUsdt is <= 0 or > 1000000m || Leverage is < 1 or > 1000m) return "ورودی یا لوریج معتبر نیست.";
        if (Direction == "Long" ? !(StopLoss < EntryPrice && TakeProfit > EntryPrice && ExpirePrice > EntryPrice)
            : !(StopLoss > EntryPrice && TakeProfit < EntryPrice && ExpirePrice < EntryPrice))
            return "تارگت و اکسپایر باید در سمت سود و استاپ در سمت زیانِ ورود باشند.";
        return null;
    }
}

public static class ManualSignals
{
    public const string Origin = "ManualChart";
    public static ServerSignal Build(ManualSignalRequest input, BybitInstrumentRules rules)
    {
        if (input.Validate() is { } error) throw new ArgumentException(error);
        if (!input.Symbol.Equals(rules.Symbol, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("قوانین نماد مطابقت ندارد.");
        if (BybitLeverageRules.Normalize(input.Leverage, rules) != input.Leverage) throw new ArgumentException("لوریج انتخاب‌شده با محدودیت یا گام لوریج این ارز سازگار نیست.");
        decimal Round(decimal p) => BybitOrderPreviewBuilder.RoundToStep(p, rules.TickSize);
        var rounded = input with { EntryPrice = Round(input.EntryPrice), TakeProfit = Round(input.TakeProfit), StopLoss = Round(input.StopLoss), ExpirePrice = Round(input.ExpirePrice) };
        if (rounded.Validate() is { } roundingError) throw new ArgumentException(roundingError + " فاصله خطوط از گام قیمت کمتر است.");
        var quantity = BybitOrderPreviewBuilder.FloorToStep(input.PositionSizeUsdt * input.Leverage / rounded.EntryPrice, rules.QuantityStep);
        if (quantity < rules.MinimumOrderQuantity || quantity * rounded.EntryPrice < rules.MinimumNotionalValue)
            throw new ArgumentException("ورودی برای حداقل اندازه سفارش این ارز کافی نیست.");
        var distance = rounded.TakeProfit - rounded.EntryPrice;
        return new ServerSignal {
            Id = input.RequestId, Origin = Origin, Symbol = rules.Symbol, Direction = input.Direction,
            Timeframe = input.Timeframe, ChartMode = "Candles", PositionSizeUsdt = input.PositionSizeUsdt,
            Leverage = input.Leverage, LeverageSource = "Manual", Quantity = quantity,
            EntryPrice = rounded.EntryPrice, TakeProfit = rounded.TakeProfit, StopLoss = rounded.StopLoss,
            Ceiling = Math.Max(rounded.ExpirePrice, rounded.StopLoss), Floor = Math.Min(rounded.ExpirePrice, rounded.StopLoss),
            ExpirePrice = rounded.ExpirePrice, ExpireActivationPrice = rounded.EntryPrice + distance * .25m,
            EarlyStopLimit = rounded.EntryPrice + distance * .20m, StopLoss2 = rounded.EntryPrice + distance * .50m,
            RiskFreeStopMarket = rounded.EntryPrice + distance * .20m, RiskFreePrice = rounded.EntryPrice + distance * .75m,
            OrderLinkId = $"phoenix-{input.RequestId:N}"[..36], CreatedAtUtc = DateTime.UtcNow
        };
    }
    public static IReadOnlyList<BybitKline> AggregateQuarters(IEnumerable<BybitKline> monthly) => monthly
        .OrderBy(c => c.OpenTime)
        .GroupBy(c => { var t = DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTime); return new DateTimeOffset(t.Year, (t.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(); })
        .Select(g => new BybitKline(g.Key, g.First().Open, g.Max(c => c.High), g.Min(c => c.Low), g.Last().Close, g.Sum(c => c.Volume))).ToArray();

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/manual-signals/candles", async (string symbol, string? interval, BybitDemoClient bybit, BybitInstrumentCatalog catalog, CancellationToken token) => {
            if (!await catalog.ContainsAsync(symbol, token)) return Results.BadRequest(new { error = "نماد معتبر نیست." });
            if (interval is not ("1" or "5" or "15" or "30" or "60" or "240" or "D" or "W" or "M" or "3M")) return Results.BadRequest(new { error = "تایم‌فریم معتبر نیست." });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try { var candles = await bybit.GetKlinesAsync(symbol, interval == "3M" ? "M" : interval, 500, timeout.Token); if (interval == "3M") candles = AggregateQuarters(candles); var rules = await bybit.GetInstrumentRulesAsync(symbol, timeout.Token);
                return Results.Ok(new { symbol, interval, candles, tickSize = rules.TickSize, quantityStep = rules.QuantityStep, maximumLeverage = rules.MaximumLeverage }); }
            catch (Exception) when (!token.IsCancellationRequested) { return Results.Json(new { error = "دریافت نمودار ناموفق بود؛ دوباره تلاش کن." }, statusCode:503); }
        });
        app.MapPost("/api/manual-signals/preview", async (ManualSignalRequest input, BybitDemoClient bybit, BybitInstrumentCatalog catalog, CancellationToken token) => {
            if (input.Validate() is { } error) return Results.BadRequest(new { error });
            try { if (!await catalog.ContainsAsync(input.Symbol, token)) return Results.BadRequest(new { error = "نماد معتبر نیست." });
                return Results.Ok(Build(input, await bybit.GetInstrumentRulesAsync(input.Symbol, token))); }
            catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
        });
        app.MapPost("/api/manual-signals", async (ManualSignalRequest input, HttpRequest request, SignalSubmissionService submission, CancellationToken token) => {
            var key = Environment.GetEnvironmentVariable("PHOENIX_PANEL_KEY");
            if (!string.IsNullOrWhiteSpace(key) && request.Headers["X-Phoenix-Key"] != key) return Results.Json(new { error = "کلید ورود پنل صحیح نیست." }, statusCode:401);
            PhoenixSessionAuth.TryGetIdentity(request, out var identity);
            return await submission.SubmitManualAsync(input, identity.Username, token);
        });
        app.MapGet("/api/manual-signals/results", async (int? offset, ServerOrderStore store, CancellationToken token) =>
            Results.Ok(await store.GetManualReportAsync(Math.Max(0, offset ?? 0), token)));
    }
}

public sealed record ManualSignalReport(int Total, int Active, int Target, int StopLoss, int RiskFree, int Expired,
    IReadOnlyList<SignalHistoryItem> Rows, bool HasMore);

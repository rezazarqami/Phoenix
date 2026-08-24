using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;
using Phoenix.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(BybitDemoOptions.FromEnvironment());
builder.Services.AddSingleton<BybitDemoClient>();
builder.Services.AddSingleton<ServerState>();
builder.Services.AddSingleton<ServerOrderStore>();
builder.Services.AddSingleton<BybitInstrumentCatalog>();
builder.Services.AddHttpClient<MarketCapCatalog>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Phoenix-Signal-Lab/1.0");
});
builder.Services.AddSingleton<PhoenixCredentialStore>();
builder.Services.AddSingleton<ElliottWaveAnalyzer>();
builder.Services.AddSingleton<SignalCandidateFinder>();
builder.Services.AddSingleton<SignalSubmissionService>();
builder.Services.AddSingleton<SignalPlanPreviewer>();
builder.Services.AddSingleton<SignalBatchService>();
builder.Services.AddSingleton(TelegramOptions.FromEnvironment());
builder.Services.AddSingleton<TelegramNotifier>();
builder.Services.AddSingleton(PublicSignalTelegramOptions.FromEnvironment());
builder.Services.AddSingleton<PublicSignalNotifier>();
builder.Services.AddSingleton(Strategy2Options.FromEnvironment());
builder.Services.AddSingleton<Strategy2Runtime>();
builder.Services.AddSingleton(Strategy2TelegramOptions.FromEnvironment());
builder.Services.AddSingleton<Strategy2TelegramNotifier>();
builder.Services.AddSingleton<StrategyCalculator>();
builder.Services.AddHostedService<DemoOrderWorker>();
builder.Services.AddHostedService<BybitEntryWebSocketWorker>();
builder.Services.AddHostedService<TelegramCommandWorker>();
builder.Services.AddSingleton<Strategy2Worker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<Strategy2Worker>());
builder.Services.AddHostedService<Strategy2EntryWebSocketWorker>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    var analysisAsset = path is "/analysis.css" or "/analysis.js" or "/lab-nav.css" or "/signal-lab.css" or "/signal-range.css" or "/signal-symbol.css" or "/signal-loading.css" or "/signal-drawing.css" or "/signal-drawing.js" or "/signal-lab.js" or "/crypto-market.css" or "/crypto-market.js" or
        "/vendor/lightweight-charts.standalone.production.js";
    var analysisPath = context.Request.Path.StartsWithSegments("/analysis") ||
                       context.Request.Path.StartsWithSegments("/api/analysis") || analysisAsset;
    var publicAnalysisPath = path is "/analysis/login" or "/analysis/login.html" or
        "/analysis-login.css" or "/analysis-login.js" or "/api/analysis/auth/login";
    if (analysisPath)
    {
        if (publicAnalysisPath)
        {
            await next();
            return;
        }
        if (!AnalysisSessionAuth.CredentialsConfigured(out _, out _))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Analysis access credentials are not configured." });
            return;
        }
        if (!AnalysisSessionAuth.IsValid(context.Request))
        {
            if (context.Request.Path.StartsWithSegments("/api"))
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                context.Response.Redirect("/analysis/login");
            return;
        }
        await next();
        return;
    }
    var publicPath = path is "/login" or "/login.html" or "/login.css" or "/login.js" or
        "/login-analysis-link.css" or "/api/auth/login" or
        "/analysis-login.css" or "/analysis-login.js";
    if (publicPath)
    {
        await next();
        return;
    }
    if (!PhoenixSessionAuth.CredentialsConfigured(out _, out _))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { error = "Phoenix access credentials are not configured." });
        return;
    }
    if (!PhoenixSessionAuth.IsValid(context.Request))
    {
        if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else
            context.Response.Redirect("/login");
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "phoenix-web" }));
app.MapGet("/login", () => Results.File(Path.Combine(app.Environment.WebRootPath, "login.html"), "text/html; charset=utf-8"));
app.MapGet("/analysis/login", () => Results.File(Path.Combine(app.Environment.WebRootPath, "analysis-login.html"), "text/html; charset=utf-8"));
app.MapGet("/analysis", () => Results.File(Path.Combine(app.Environment.WebRootPath, "analysis.html"), "text/html; charset=utf-8"));
app.MapGet("/analysis/signals", () => Results.File(Path.Combine(app.Environment.WebRootPath, "signal-lab.html"), "text/html; charset=utf-8"));
app.MapGet("/analysis/coins", () => Results.File(Path.Combine(app.Environment.WebRootPath, "crypto-market.html"), "text/html; charset=utf-8"));
app.MapPost("/api/auth/login", (LoginRequest request, HttpResponse response) =>
{
    if (!PhoenixSessionAuth.CredentialsMatch(request.Username, request.Password))
        return Results.Json(new { error = "نام کاربری یا رمز عبور صحیح نیست." }, statusCode: StatusCodes.Status401Unauthorized);
    var token = PhoenixSessionAuth.CreateToken(request.Username, request.Password);
    response.Cookies.Append(PhoenixSessionAuth.CookieName, token, new CookieOptions
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = false,
        MaxAge = TimeSpan.FromHours(12), Path = "/"
    });
    return Results.Ok(new { loggedIn = true });
});
app.MapPost("/api/auth/logout", (HttpResponse response) =>
{
    response.Cookies.Delete(PhoenixSessionAuth.CookieName, new CookieOptions { Path = "/" });
    return Results.Ok(new { loggedOut = true });
});
app.MapPost("/api/analysis/auth/login", (LoginRequest request, HttpRequest httpRequest, HttpResponse response) =>
{
    if (!AnalysisSessionAuth.CredentialsConfigured(out _, out _))
        return Results.Json(new { error = "اطلاعات ورود بخش تحلیل هنوز روی سرور تنظیم نشده است." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!AnalysisSessionAuth.CredentialsMatch(request.Username, request.Password))
        return Results.Json(new { error = "نام کاربری یا رمز عبور صحیح نیست." }, statusCode: StatusCodes.Status401Unauthorized);
    var token = AnalysisSessionAuth.CreateToken(request.Username, request.Password);
    response.Cookies.Append(AnalysisSessionAuth.CookieName, token, new CookieOptions
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = httpRequest.IsHttps,
        MaxAge = TimeSpan.FromHours(12), Path = "/"
    });
    return Results.Ok(new { loggedIn = true });
});
app.MapPost("/api/analysis/auth/logout", (HttpResponse response) =>
{
    response.Cookies.Delete(AnalysisSessionAuth.CookieName, new CookieOptions { Path = "/" });
    return Results.Ok(new { loggedOut = true });
});
app.MapGet("/api/status", (ServerState state, BybitDemoOptions options, TelegramNotifier telegram) => Results.Ok(new
{
    publicApiConnected = state.PublicApiConnected,
    demoAuthenticated = state.DemoAuthenticated,
    tradingEnvironment = options.EnvironmentName,
    credentialsConfigured = options.HasCredentials,
    lastPrice = state.LastPrice,
    lastUpdatedUtc = state.LastUpdatedUtc,
    error = state.Error,
    panelLocked = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHOENIX_PANEL_KEY")),
    tradingEnabled = DemoOrderWorker.IsTradingEnabled(options),
    telegramConfigured = telegram.IsConfigured
}));

app.MapGet("/api/signals", async (ServerOrderStore store, BybitDemoClient bybit, CancellationToken token) =>
{
    var signals = await store.GetAllAsync(token);
    foreach (var signal in signals.Where(x => x.Status == "Pending" && x.LeverageSource != "PhoenixFormula"))
    {
        try
        {
            var rules = await bybit.GetInstrumentRulesAsync(signal.Symbol, token);
            signal.ApplyPhoenixLeverage(rules);
            await store.UpdateAsync(signal, token);
        }
        catch { /* A temporary Bybit failure must not make the signal panel unavailable. */ }
    }
    return Results.Ok(signals.OrderByDescending(x => x.CreatedAtUtc));
});

app.MapGet("/api/history", async (int? days, int? limit, ServerOrderStore store, CancellationToken token) =>
    Results.Ok(await store.GetHistoryAsync(days ?? 30, limit ?? 1000, token)));

app.MapGet("/api/strategy2/status", async (Strategy2Runtime strategy2, CancellationToken token) =>
{
    if (!strategy2.Options.Enabled)
        return Results.Ok(new { enabled = false, authenticated = false, availableBalance = (decimal?)null });
    try
    {
        var status = await strategy2.Client.CheckConnectionAsync(token);
        decimal? balance = status.Authenticated ? await strategy2.Client.GetAvailableBalanceAsync(token) : null;
        return Results.Ok(new { enabled = true, authenticated = status.Authenticated, availableBalance = balance });
    }
    catch (Exception exception)
    {
        return Results.Ok(new { enabled = true, authenticated = false, error = exception.Message });
    }
});

app.MapGet("/api/strategy2/signals", async (Strategy2Runtime strategy2, CancellationToken token) =>
    Results.Ok((await strategy2.Store.GetAllAsync(token)).OrderByDescending(x => x.CreatedAtUtc)));

app.MapGet("/api/strategy2/history", async (int? days, int? limit, Strategy2Runtime strategy2,
    CancellationToken token) =>
    Results.Ok(await strategy2.Store.GetHistoryAsync(days ?? 30, limit ?? 1000, token)));

app.MapGet("/api/instruments", async (BybitInstrumentCatalog catalog, CancellationToken token) =>
    Results.Ok(new { symbols = await catalog.GetAsync(token) }));

app.MapGet("/api/analysis/instruments", async (BybitInstrumentCatalog catalog, CancellationToken token) =>
    Results.Ok(new { symbols = await catalog.GetAsync(token) }));

app.MapGet("/api/analysis/coins", async (MarketCapCatalog catalog, CancellationToken token) =>
{
    try { return Results.Ok(new { assets = await catalog.GetAsync(token) }); }
    catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapGet("/api/analysis/signal-batch", (SignalBatchService batches) => Results.Ok(batches.Status));
app.MapPost("/api/analysis/signal-batch", (StartSignalBatchRequest request, SignalBatchService batches) =>
{
    if (request.Count is < 1 or > 200) return Results.BadRequest(new { error = "تعداد باید بین ۱ تا ۲۰۰ باشد." });
    if (request.PositionSizeUsdt <= 0) return Results.BadRequest(new { error = "مقدار ورودی باید بیشتر از صفر باشد." });
    var directionFilter = string.IsNullOrWhiteSpace(request.DirectionFilter) ? "All" : request.DirectionFilter;
    if (directionFilter is not ("All" or "Long" or "Short")) return Results.BadRequest(new { error = "فیلتر جهت معتبر نیست." });
    var chartFilter = string.IsNullOrWhiteSpace(request.ChartFilter) ? "All" : request.ChartFilter;
    if (chartFilter is not ("All" or "Candles" or "Line")) return Results.BadRequest(new { error = "نوع نمودار معتبر نیست." });
    var timeframeFilter = string.IsNullOrWhiteSpace(request.TimeframeFilter) ? "All" : request.TimeframeFilter;
    if (timeframeFilter is not ("All" or "15" or "60" or "240"))
        return Results.BadRequest(new { error = "تایم‌فریم معتبر نیست." });
    return batches.Start(request.Count, request.PositionSizeUsdt, directionFilter, chartFilter,
            timeframeFilter, out var error)
        ? Results.Accepted(value: batches.Status)
        : Results.Conflict(new { error });
});

app.MapGet("/api/analysis/candles", async (string symbol, string? interval, int? limit, int? depth,
    decimal? deviation, BybitDemoClient bybit, BybitInstrumentCatalog catalog,
    ElliottWaveAnalyzer analyzer, CancellationToken token) =>
{
    try
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (!await catalog.ContainsAsync(symbol, token))
            return Results.BadRequest(new { error = "نماد انتخاب‌شده در بازار فعال Bybit Futures وجود ندارد." });
        var candles = await bybit.GetKlinesAsync(symbol, interval ?? "60", Math.Clamp(limit ?? 500, 50, 1000), token);
        var analysis = analyzer.Analyze(candles, Math.Clamp(depth ?? 5, 2, 20), deviation ?? 0.6m);
        return Results.Ok(new { symbol, interval = interval ?? "60", candles, analysis });
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/api/analysis/signal-candidate", async (string symbol, string? interval, string? chartType, int? depth, long? from, long? to,
    decimal? positionSizeUsdt, BybitDemoClient bybit, BybitInstrumentCatalog catalog,
    SignalCandidateFinder finder, CancellationToken token) =>
{
    try
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (!await catalog.ContainsAsync(symbol, token))
            return Results.BadRequest(new { error = "نماد انتخاب‌شده در بازار فعال Bybit Futures وجود ندارد." });
        var selectedInterval = interval ?? "60";
        var candles = await bybit.GetKlinesAsync(symbol, selectedInterval, 1000, token);
        var selectedCandles = candles.Where(candle =>
            (!from.HasValue || candle.OpenTime >= from.Value * 1000L) &&
            (!to.HasValue || candle.OpenTime <= to.Value * 1000L)).ToArray();
        if (selectedCandles.Length < 30)
            return Results.BadRequest(new { error = "محدوده نمودار خیلی کوچک است؛ حداقل ۳۰ کندل را نمایش دهید." });
        var rules = await bybit.GetInstrumentRulesAsync(symbol, token);
        var candidate = finder.Find(symbol, selectedInterval, selectedCandles, rules,
            Math.Clamp(positionSizeUsdt ?? 25m, 1m, 1_000_000m), depth ?? 5,
            string.Equals(chartType, "line", StringComparison.OrdinalIgnoreCase));
        return Results.Ok(new { candidate, candles });
    }
    catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapPost("/api/analysis/signals/confirm", async (ConfirmSignalRequest request,
    SignalSubmissionService submission, CancellationToken token) =>
{
    if (!request.Confirmed)
        return Results.BadRequest(new { error = "ثبت سیگنال نیازمند تأیید صریح است." });
    return await submission.SubmitAsync(request.Signal, token);
});

app.MapPost("/api/analysis/signal-preview", async (SignalRequest request,
    SignalPlanPreviewer previewer, CancellationToken token) =>
{
    try { return Results.Ok(await previewer.PreviewAsync(request, token)); }
    catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapGet("/api/instruments/{symbol}/limits", async (string symbol, BybitDemoClient bybit, CancellationToken token) =>
{
    try
    {
        var rules = await bybit.GetInstrumentRulesAsync(symbol, token);
        return Results.Ok(new { symbol = rules.Symbol, maximumLeverage = rules.MaximumLeverage });
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPost("/api/signals", async (SignalRequest request, HttpRequest httpRequest,
    SignalSubmissionService submission, CancellationToken token) =>
{
    var panelKey = Environment.GetEnvironmentVariable("PHOENIX_PANEL_KEY");
    if (!string.IsNullOrWhiteSpace(panelKey) && httpRequest.Headers["X-Phoenix-Key"] != panelKey)
        return Results.Json(new { error = "کلید ورود پنل صحیح نیست." }, statusCode: StatusCodes.Status401Unauthorized);

    return await submission.SubmitAsync(request, token);
});

app.MapDelete("/api/signals/{id:guid}", async (Guid id, ServerOrderStore store, BybitDemoClient bybit,
    TelegramNotifier telegram, CancellationToken token) =>
{
    var signal = (await store.GetAllAsync(token)).SingleOrDefault(x => x.Id == id);
    if (signal is null) return Results.NotFound(new { error = "سفارش پیدا نشد." });
    if (signal.Status == "Submitting")
        return Results.Conflict(new { error = "سفارش در حال ارسال است؛ چند ثانیه بعد دوباره تلاش کنید." });
    var cancelledAtBybit = signal.Status == "Submitted" && !string.IsNullOrWhiteSpace(signal.BybitOrderId);
    if (cancelledAtBybit)
    {
        try { await bybit.CancelOrderAsync(signal.Symbol, signal.BybitOrderId!, token); }
        catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
    }
    await store.RemoveAsync(id, token);
    await telegram.RemovedAsync(signal, cancelledAtBybit, token);
    return Results.NoContent();
});

app.MapPost("/api/telegram/test", async (HttpRequest httpRequest, TelegramNotifier telegram, CancellationToken token) =>
{
    var panelKey = Environment.GetEnvironmentVariable("PHOENIX_PANEL_KEY");
    if (!string.IsNullOrWhiteSpace(panelKey) && httpRequest.Headers["X-Phoenix-Key"] != panelKey)
        return Results.Unauthorized();
    return telegram.IsConfigured && await telegram.SendTestAsync(token)
        ? Results.Ok(new { sent = true })
        : Results.BadRequest(new { error = "Telegram is not configured or the test message failed." });
});

app.MapPost("/api/auth/change", async (ChangeCredentialsRequest request, PhoenixCredentialStore credentials,
    CancellationToken token) =>
{
    var error = request.Validate();
    if (error is not null)
        return Results.BadRequest(new { error });

    await credentials.UpdateAsync(request.Username.Trim(), request.Password, token);
    return Results.Ok(new { changed = true });
});

app.MapFallbackToFile("index.html");
app.Run();

namespace Phoenix.Web
{
    public sealed class ServerState
    {
        public bool PublicApiConnected { get; set; }
        public bool DemoAuthenticated { get; set; }
        public decimal? LastPrice { get; set; }
        public DateTime? LastUpdatedUtc { get; set; }
        public string? Error { get; set; }
    }

    public sealed record SignalRequest(string Symbol, string Direction, decimal Ceiling, decimal Floor,
        decimal PositionSizeUsdt)
    {
        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Symbol) || Symbol.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                return "نماد معتبر نیست.";
            if (Direction is not ("Long" or "Short"))
                return "جهت باید Long یا Short باشد.";
            if (Ceiling <= Floor)
                return "سقف باید بزرگ‌تر از کف باشد.";
            if (PositionSizeUsdt <= 0)
                return "سرمایه باید بیشتر از صفر باشد.";
            return null;
        }
    }

    public sealed record ChangeCredentialsRequest(string Username, string Password, string ConfirmPassword)
    {
        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Username) || Username.Length is < 3 or > 32 ||
                Username.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
                return "نام کاربری باید ۳ تا ۳۲ کاراکتر و فقط شامل حروف انگلیسی، عدد، خط تیره، زیرخط یا نقطه باشد.";
            if (Password.Length is < 6 or > 128)
                return "رمز عبور باید حداقل ۶ کاراکتر باشد.";
            if (Password != ConfirmPassword)
                return "تکرار رمز عبور یکسان نیست.";
            if (Password.Contains('\n') || Password.Contains('\r'))
                return "رمز عبور معتبر نیست.";
            return null;
        }
    }

    public sealed record LoginRequest(string Username, string Password);
    public sealed record ConfirmSignalRequest(bool Confirmed, SignalRequest Signal);
}

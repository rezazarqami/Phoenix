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
builder.Services.AddSingleton<PhoenixCredentialStore>();
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
    var publicPath = path is "/login" or "/login.html" or "/login.css" or "/login.js" or "/api/auth/login";
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

app.MapPost("/api/signals", async (SignalRequest request, HttpRequest httpRequest, ServerOrderStore store,
    StrategyCalculator calculator, BybitDemoClient bybit, BybitInstrumentCatalog catalog,
    TelegramNotifier telegram, Strategy2Runtime strategy2, Strategy2TelegramNotifier strategy2Telegram,
    CancellationToken token) =>
{
    var panelKey = Environment.GetEnvironmentVariable("PHOENIX_PANEL_KEY");
    if (!string.IsNullOrWhiteSpace(panelKey) && httpRequest.Headers["X-Phoenix-Key"] != panelKey)
        return Results.Json(new { error = "کلید ورود پنل صحیح نیست." }, statusCode: StatusCodes.Status401Unauthorized);

    var error = request.Validate();
    if (error is not null)
        return Results.BadRequest(new { error });

    if (!await catalog.ContainsAsync(request.Symbol, token))
        return Results.BadRequest(new { error = "نماد انتخاب‌شده در بازار فعال Bybit Futures وجود ندارد." });

    try
    {
        var direction = Enum.Parse<Direction>(request.Direction);
        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = request.Symbol.Trim().ToUpperInvariant(),
            Direction = direction,
            High = request.Ceiling,
            Low = request.Floor,
            PositionSizeUsdt = request.PositionSizeUsdt,
            CreatedAt = DateTime.UtcNow,
            Status = SignalStatus.WaitingEntry
        };
        signal.TradePlan = calculator.Calculate(signal);
        var rules = await bybit.GetInstrumentRulesAsync(signal.Symbol, token);
        signal.TradePlan.Leverage = BybitLeverageRules.Normalize(signal.TradePlan.Leverage, rules);
        var position = new ExecutionManager().PreparePosition(signal)
            ?? throw new InvalidOperationException("ساخت موقعیت برنامه‌ریزی‌شده ناموفق بود.");
        var preview = BybitOrderPreviewBuilder.Build(signal.Symbol, position, rules);
        var queued = ServerSignal.FromPreview(signal, preview, signal.TradePlan.Leverage);
        await store.AddAsync(queued, token);
        await telegram.SignalQueuedAsync(queued, token);
        if (strategy2.Options.Enabled)
        {
            var strategy2Signal = ServerSignal.FromPreview(signal, preview, signal.TradePlan.Leverage);
            strategy2Signal.PositionSizeUsdt = 0m;
            strategy2Signal.Quantity = 0m;
            strategy2Signal.OrderLinkId = $"s2-{strategy2Signal.Id:N}"[..35];
            await strategy2.Store.AddAsync(strategy2Signal, token);
            await strategy2Telegram.QueuedAsync(strategy2Signal, token);
        }
        return Results.Created($"/api/signals/{queued.Id}", queued);
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
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

    public sealed record SignalRequest(string Symbol, string Direction, decimal Ceiling, decimal Floor, decimal PositionSizeUsdt)
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
}

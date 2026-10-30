using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;
using Phoenix.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.AddRateLimiter(options => {
    options.AddPolicy("account-login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = async (context, token) => { context.HttpContext.Response.StatusCode = 429; context.HttpContext.Response.Headers.RetryAfter = "60"; await context.HttpContext.Response.WriteAsJsonAsync(new { error = "تلاش‌های ورود زیاد شده؛ یک دقیقه بعد دوباره امتحان کنید." }, token); };
});
builder.Services.AddSingleton<MobilePushService>();
builder.Services.AddSingleton<MobileSignalFeed>();
builder.Services.AddSingleton<BybitWalletRecovery>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BybitWalletRecovery>());
builder.Services.AddHostedService<MobileSignalWorker>();
builder.Services.AddHttpClient("MobilePush", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton(BybitDemoOptions.FromEnvironment());
builder.Services.AddSingleton<BybitDemoClient>();
builder.Services.AddSingleton<ServerState>();
builder.Services.AddSingleton<ServerOrderStore>();
builder.Services.AddSingleton<SignalCancellationService>();
builder.Services.AddSingleton<BybitInstrumentCatalog>();
builder.Services.AddSingleton<ProposalLeverageCache>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ProposalLeverageCache>());
builder.Services.AddHttpClient("MarketCap", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Phoenix-Signal-Lab/1.0");
});
builder.Services.AddSingleton(provider => new MarketCapCatalog(
    provider.GetRequiredService<IHttpClientFactory>().CreateClient("MarketCap"),
    provider.GetRequiredService<BybitInstrumentCatalog>(), provider.GetRequiredService<ServerOrderStore>()));
builder.Services.AddSingleton<PhoenixCredentialStore>();
builder.Services.AddSingleton<PhoenixUserStore>();
builder.Services.AddSingleton<TelegramAccessStore>();
builder.Services.AddSingleton<ElliottWaveAnalyzer>();
builder.Services.AddSingleton<ElliottCountStore>();
builder.Services.AddSingleton<SignalCandidateFinder>();
builder.Services.AddSingleton<ShadowSignalRuntime>();
builder.Services.AddSingleton<SignalLearningService>();
builder.Services.AddSingleton<SignalSimilarityService>();
builder.Services.AddSingleton<ProfessionalSignalAnalysisService>();
builder.Services.AddSingleton<EntrySignalReviewService>();
builder.Services.AddSingleton<SignalSubmissionService>();
builder.Services.AddSingleton<SignalPlanPreviewer>();
builder.Services.AddSingleton<SignalBatchService>();
builder.Services.AddSingleton<ReviewArchiveStore>();
builder.Services.AddSingleton(TelegramOptions.FromEnvironment());
builder.Services.AddSingleton<TelegramNotifier>();
builder.Services.AddSingleton(DedicatedTelegramOptions.FromEnvironment());
builder.Services.AddSingleton<DedicatedTelegramNotifier>();
builder.Services.AddSingleton(PublicSignalTelegramOptions.FromEnvironment());
builder.Services.AddSingleton<PublicSignalNotifier>();
builder.Services.AddSingleton(Strategy2Options.FromEnvironment());
builder.Services.AddSingleton<Strategy2Runtime>();
builder.Services.AddSingleton(Strategy2TelegramOptions.FromEnvironment());
builder.Services.AddSingleton<Strategy2TelegramNotifier>();
builder.Services.AddSingleton<StrategyCalculator>();
builder.Services.AddSingleton(OpenAiOptions.FromEnvironment());
builder.Services.AddHttpClient<AiSignalParser>(client =>
{
    client.BaseAddress = new Uri("https://api.openai.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHostedService<DemoOrderWorker>();
builder.Services.AddHostedService<ShadowSignalWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SignalLearningService>());
builder.Services.AddHostedService<HistoricalEntryBackfillWorker>();
builder.Services.AddHostedService<PublicSignalNotificationWorker>();
builder.Services.AddHostedService<PublicSignalCommandWorker>();
builder.Services.AddHostedService<PublicSignalImageBackfillWorker>();
builder.Services.AddSingleton<BulkPositionService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BulkPositionService>());
builder.Services.AddHostedService<BybitEntryWebSocketWorker>();
builder.Services.AddHostedService<TelegramCommandWorker>();
builder.Services.AddSingleton<Strategy2Worker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<Strategy2Worker>());
builder.Services.AddHostedService<Strategy2EntryWebSocketWorker>();

var app = builder.Build();
var ownerOnlyAccess = builder.Configuration.GetValue("OwnerOnlyAccess", true);
if (ownerOnlyAccess)
{
    if (!PhoenixSessionAuth.CredentialsConfigured(out var primaryOwner, out _))
        throw new InvalidOperationException("Owner-only access requires configured primary credentials.");
    var userStore = app.Services.GetRequiredService<PhoenixUserStore>();
    var removed = await userStore.DeleteAllAdditionalAsync(CancellationToken.None);
    await userStore.WriteOwnerOnlyStatusAsync(primaryOwner, removed, CancellationToken.None);
    app.Logger.LogInformation("Owner-only access enabled; removed {Count} additional accounts.", removed);
}
app.UseForwardedHeaders();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var users = context.RequestServices.GetRequiredService<PhoenixUserStore>();
    var path = context.Request.Path.Value;
    if (path is "/" or "/index.html")
    {
        context.Response.Redirect("/phoenix");
        return;
    }
    if ((context.Request.Path.StartsWithSegments("/api/personal") &&
        !context.Request.Path.StartsWithSegments("/api/personal/mobile")) ||
        path is "/personal.css" or "/personal.js" or "/personal-core.js" or "/personal-storage.js" or "/orbit-simple.js" or "/mobile.js")
    {
        context.Response.StatusCode = 404;
        return;
    }

    if (path is "/analysis/login" or "/analysis/login.html" or "/analysis-login.html")
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect("/analysis/coins");
        return;
    }
    var analysisAsset = context.Request.Path.StartsWithSegments("/analysis-assets") ||
        path is "/analysis.css" or "/analysis.js" or "/lab-nav.css" or "/analysis-brand.css" or "/professional-ui.css" or "/signal-lab.css" or "/signal-range.css" or "/signal-symbol.css" or "/signal-loading.css" or "/signal-drawing.css" or "/signal-drawing.js" or "/signal-lab.js" or "/crypto-market.css" or "/batch-timed.css" or "/results-report.css" or "/crypto-market.js" or "/crypto-ice.css" or "/crypto-ice.js" or "/in-app-review.js" or "/in-app-review.css" or "/signal-batch-controls.js" or
        "/vendor/lightweight-charts.standalone.production.js";
    var analysisPath = context.Request.Path.StartsWithSegments("/analysis") ||
                       context.Request.Path.StartsWithSegments("/api/analysis") || analysisAsset;
    var publicAnalysisPath = context.Request.Path.StartsWithSegments("/analysis-assets") ||
        path is "/analysis/login" or "/analysis/login.html" or
        "/analysis-login.css" or "/analysis-login.js" or "/api/analysis/auth/login";
    if (analysisPath)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
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
        if (!AnalysisSessionAuth.TryGetIdentity(context.Request, out var identity) ||
            !identity.IsAdmin && (ownerOnlyAccess || !await users.ExistsAsync(identity.Username, identity.ViewerOnly, context.RequestAborted)))
        {
            if (context.Request.Path.StartsWithSegments("/api"))
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                context.Response.Redirect("/login");
            return;
        }
        PhoenixSessionAuth.RenewSessionCookie(context, identity);
        if (identity.ViewerOnly && context.Request.Method is not ("GET" or "HEAD") &&
            path is not "/api/analysis/auth/logout")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "این حساب فقط امکان مشاهده دارد." });
            return;
        }
        await next();
        return;
    }
    var publicPath = path is "/login" or "/login.html" or "/login.css" or "/login-ice.css" or "/login.js" or
        "/login-analysis-link.css" or "/login-gold.css" or "/viewer-mode.js" or "/api/auth/login" or
        "/analysis-login.css" or "/analysis-login.js" or
        "/manifest.webmanifest" or "/sw.js" ||
        context.Request.Path.StartsWithSegments("/icons") || path is "/fonts/Vazirmatn-variable.woff2";
    if (publicPath)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        await next();
        return;
    }
    if (!PhoenixSessionAuth.CredentialsConfigured(out _, out _))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { error = "Phoenix access credentials are not configured." });
        return;
    }
    if (!PhoenixSessionAuth.TryGetIdentity(context.Request, out var phoenixIdentity) ||
        !phoenixIdentity.IsAdmin && (ownerOnlyAccess || !await users.ExistsAsync(phoenixIdentity.Username, phoenixIdentity.ViewerOnly, context.RequestAborted)))
    {
        if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else
            context.Response.Redirect(path == "/notifications" ? "/login?next="+Uri.EscapeDataString(context.Request.Path+context.Request.QueryString) : "/login");
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    PhoenixSessionAuth.RenewSessionCookie(context, phoenixIdentity);
    if (phoenixIdentity.ViewerOnly && context.Request.Method is not ("GET" or "HEAD") &&
        path is not "/api/auth/logout")
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "این حساب فقط امکان مشاهده دارد." });
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/phoenix"));
app.MapGet("/index.html", () => Results.Redirect("/phoenix"));
app.MapGet("/phoenix", () => Results.File(Path.Combine(app.Environment.WebRootPath, "phoenix.html"), "text/html; charset=utf-8"));
app.MapMobileEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "phoenix-web" }));
app.MapGet("/notifications", () => Results.File(Path.Combine(app.Environment.WebRootPath,"notifications.html"),"text/html; charset=utf-8"));
app.MapGet("/login", () => Results.File(Path.Combine(app.Environment.WebRootPath, "login.html"), "text/html; charset=utf-8"));

app.MapGet("/analysis", (HttpRequest request) => request.Query["v"] == "20260827-4"
    ? Results.File(Path.Combine(app.Environment.WebRootPath, "analysis.html"), "text/html; charset=utf-8")
    : Results.Redirect("/analysis?v=20260827-4"));
app.MapGet("/analysis/signals", (HttpRequest request) => request.Query["v"] == "20260827-4"
    ? Results.File(Path.Combine(app.Environment.WebRootPath, "signal-lab.html"), "text/html; charset=utf-8")
    : Results.Redirect("/analysis/signals?v=20260827-4"));
app.MapGet("/analysis/coins", (HttpRequest request) => request.Query["v"] == "20260902-1"
    ? Results.File(Path.Combine(app.Environment.WebRootPath, "crypto-market.html"), "text/html; charset=utf-8")
    : Results.Redirect("/analysis/coins?v=20260902-1"));
app.MapPost("/api/auth/login", async (LoginRequest request, HttpResponse response, PhoenixUserStore users,
    CancellationToken cancellationToken) =>
{
    var admin = PhoenixSessionAuth.CredentialsMatch(request.Username, request.Password);
    var user = admin || ownerOnlyAccess ? null : await users.AuthenticateAsync(request.Username, request.Password, cancellationToken);
    if (!admin && user is null)
        return Results.Json(new { error = "نام کاربری یا رمز عبور صحیح نیست." }, statusCode: StatusCodes.Status401Unauthorized);
    var viewerOnly = !admin && user!.ViewerOnly;
    var token = PhoenixSessionAuth.CreateToken(request.Username, viewerOnly, admin);
    PhoenixSessionAuth.WriteSessionCookie(response, token);
    return Results.Ok(new { loggedIn = true, viewerOnly, isAdmin = admin });
}).RequireRateLimiting("account-login");
app.MapPost("/api/auth/logout", (HttpResponse response) =>
{
    PhoenixSessionAuth.ClearSessionCookies(response);
    return Results.Ok(new { loggedOut = true });
});
app.MapPost("/api/analysis/auth/login", () =>
    Results.Json(new { error = "ورود تحلیل با فونیکس یکپارچه شده است؛ از صفحه ورود اصلی وارد شوید." }, statusCode: 410));
app.MapPost("/api/analysis/auth/logout", (HttpResponse response) =>
{
    PhoenixSessionAuth.ClearSessionCookies(response);
    return Results.Ok(new { loggedOut = true });
});
app.MapGet("/api/auth/me", (HttpRequest request) =>
{
    PhoenixSessionAuth.TryGetIdentity(request, out var identity);
    return Results.Ok(new { identity.Username, identity.ViewerOnly, identity.IsAdmin });
});
app.MapGet("/api/analysis/auth/me", (HttpRequest request) =>
{
    AnalysisSessionAuth.TryGetIdentity(request, out var identity);
    return Results.Ok(new { identity.Username, identity.ViewerOnly, identity.IsAdmin });
});
app.MapGet("/api/users", async (HttpRequest request, PhoenixUserStore users, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin)
        return Results.Forbid();
    var items = (await users.GetAllAsync(token)).Select(user => new
    {
        user.Username, user.ViewerOnly, user.CreatedAtUtc
    });
    return Results.Ok(items);
});
app.MapPost("/api/users", async (CreateUserRequest request, HttpRequest httpRequest, PhoenixUserStore users, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(httpRequest, out var identity) || !identity.IsAdmin)
        return Results.Forbid();
    if (ownerOnlyAccess)
        return Results.Json(new { error = "ورود فقط برای حساب اصلی فعال است؛ ایجاد کاربر فرعی بسته شده است." }, statusCode: StatusCodes.Status403Forbidden);
    var error = request.Validate();
    if (error is not null) return Results.BadRequest(new { error });
    if (PhoenixSessionAuth.CredentialsConfigured(out var adminUsername, out _) &&
        string.Equals(adminUsername, request.Username, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "این نام کاربری متعلق به مدیر اصلی است." });
    try
    {
        await users.AddAsync(request.Username.Trim(), request.Password, request.ViewerOnly, token);
        return Results.Ok(new { created = true });
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapDelete("/api/users/{username}", async (string username, HttpRequest request, PhoenixUserStore users, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin)
        return Results.Forbid();
    return await users.DeleteAsync(username, token) ? Results.Ok(new { deleted = true }) : Results.NotFound();
});
app.MapGet("/api/telegram/access", async (HttpRequest request, TelegramAccessStore access, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin)
        return Results.Forbid();
    return Results.Ok((await access.GetAllAsync(token)).OrderBy(user => user.DisplayName));
});
app.MapPost("/api/telegram/access", async (TelegramAccessRequest input, HttpRequest request,
    TelegramAccessStore access, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin)
        return Results.Forbid();
    var error = input.Validate();
    if (error is not null) return Results.BadRequest(new { error });
    try
    {
        await access.AddAsync(input.UserId, input.DisplayName, input.Username, token);
        return Results.Ok(new { created = true });
    }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapPut("/api/telegram/access/{userId:long}", async (long userId, TelegramAccessStateRequest input,
    HttpRequest request, TelegramAccessStore access, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin)
        return Results.Forbid();
    return await access.SetEnabledAsync(userId, input.Enabled, token)
        ? Results.Ok(new { updated = true }) : Results.NotFound();
});
app.MapDelete("/api/telegram/access/{userId:long}", async (long userId, HttpRequest request,
    TelegramAccessStore access, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin)
        return Results.Forbid();
    return await access.DeleteAsync(userId, token) ? Results.Ok(new { deleted = true }) : Results.NotFound();
});
// Lightweight homepage sparkline: fetch candles without running Elliott analysis.
app.MapGet("/api/market/overview", async (string? interval, BybitDemoClient bybit, CancellationToken token) =>
{
    interval ??= "60";
    if (interval is not ("1" or "60" or "D" or "W"))
        return Results.BadRequest(new { error = "تایم‌فریم معتبر نیست." });
    try
    {
        var candles = await bybit.GetKlinesAsync("BTCUSDT", interval, 50, token);
        return Results.Ok(new { interval, closes = candles.Select(x => x.Close).ToArray() });
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
    catch { return Results.Json(new { error = "دادهٔ نمودار در دسترس نیست." }, statusCode: 503); }
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
    telegramConfigured = telegram.IsConfigured,
    aiConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
}));

app.MapPost("/api/ai/parse-signal", async (AiSignalTextRequest request, AiSignalParser parser,
    CancellationToken token) =>
{
    var result = await parser.ParseAsync(request.Text, token);
    return result.Complete ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/signals", async (ServerOrderStore store, BybitDemoClient bybit,
    SignalSimilarityService similarity, CancellationToken token) =>
{
    var signals = await store.GetAllAsync(token);
    foreach (var signal in signals.Where(x => x.Status == "Pending" && x.LeverageSource is not ("PhoenixFormula" or "Manual")))
    {
        try
        {
            var rules = await bybit.GetInstrumentRulesAsync(signal.Symbol, token);
            signal.ApplyPhoenixLeverage(rules);
            await store.UpdateAsync(signal, token);
        }
        catch { /* A temporary Bybit failure must not make the signal panel unavailable. */ }
    }
    foreach (var signal in signals.Where(x => x.CompletedAtUtc is null && x.EntryTriggeredAtUtc is null && x.ProposalEvidence is null &&
        x.TechnicalFeatures is not null && x.SimilarityEvidence?.ComparisonVersion != 4))
    {
        try
        {
            var result = await similarity.CalculateAsync(signal, token);
            signal.TargetSimilarityPercent = result.TargetPercent;
            signal.StopSimilarityPercent = result.StopPercent;
            signal.SimilaritySampleCount = result.SampleCount;
            signal.SimilarityEvidence = result.Evidence;
            signal.TargetOutcomePercent = result.TargetOutcomePercent;
            signal.StopOutcomePercent = result.StopOutcomePercent;
            signal.OutcomeValidationCount = result.OutcomeValidationCount;
            await store.UpdateAsync(signal, token);
        }
        catch { /* Similarity must never make the live signal panel unavailable. */ }
    }
    return Results.Ok(signals.OrderByDescending(x => x.CreatedAtUtc));
});

app.MapGet("/api/history", async (int? days, int? limit, string? source, ServerOrderStore store, CancellationToken token) =>
    source is not (null or "normal" or "manual") ? Results.BadRequest(new {error="منشأ نتایج معتبر نیست."}) : Results.Ok(await store.GetHistoryAsync(days ?? 30, limit ?? 1000, token,source)));

app.MapGet("/api/crypto-statistics", async (string? strategy, ServerOrderStore store,
    Strategy2Runtime strategy2, HttpResponse response, CancellationToken token) =>
{
    if (strategy is not (null or "main" or "strategy2"))
        return Results.BadRequest(new { error = "استراتژی نامعتبر است." });
    response.Headers.CacheControl = "no-store";
    var selected = strategy == "strategy2" ? strategy2.Store : store;
    var items = await selected.GetCryptoStatisticsAsync(token);
    return Results.Ok(new { strategy = strategy ?? "main", updatedAtUtc = DateTime.UtcNow, items });
});

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

app.MapGet("/api/strategy2/recovery-check", async (HttpContext context, Strategy2Runtime strategy2, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(context.Request, out var identity) || !identity.IsAdmin)
        return Results.StatusCode(403);
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        var recovered = (await strategy2.Store.GetAllAsync(token)).Where(x => x.Status == "RecoveryRequired").ToArray();
        if (recovered.Length > 100) return Results.BadRequest(new { error = "تعداد رکوردهای بازیابی زیاد است؛ بررسی تخصصی لازم است." });
        var positions = await strategy2.Client.GetOpenPositionsAsync(token);
        var activeOrders = 0; var unknownOrders = 0;
        foreach (var signal in recovered)
        {
            var order = !string.IsNullOrWhiteSpace(signal.BybitOrderId)
                ? await strategy2.Client.GetOrderStatusAsync(signal.BybitOrderId, token)
                : !string.IsNullOrWhiteSpace(signal.OrderLinkId)
                    ? await strategy2.Client.GetOrderStatusByLinkIdAsync(signal.OrderLinkId, token) : null;
            if (order is null) unknownOrders++;
            else if (order.Status is not ("Filled" or "Cancelled" or "Rejected" or "Deactivated")) activeOrders++;
        }
        return Results.Ok(new { checkedAtUtc = DateTime.UtcNow, recoveredRecords = recovered.Length,
            openPositions = positions.Count, activeMatchingOrders = activeOrders, unknownOrders,
            executionPaused = recovered.Length > 0, readOnly = true });
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
    catch { return Results.Json(new { error = "بررسی صرافی کامل نشد؛ وضعیت نامعلوم را نبود سفارش تلقی نکنید. ورودها همچنان متوقف‌اند." }, statusCode:503); }
});

app.MapGet("/api/instruments", async (BybitInstrumentCatalog catalog, CancellationToken token) =>
    Results.Ok(new { symbols = await catalog.GetAsync(token) }));

app.MapGet("/api/analysis/instruments", async (BybitInstrumentCatalog catalog, CancellationToken token) =>
    Results.Ok(new { symbols = await catalog.GetAsync(token) }));

app.MapGet("/api/analysis/coins", async (MarketCapCatalog catalog, CancellationToken token) =>
{
    try { return Results.Ok(new { assets = await catalog.GetAsync(token) }); }
    catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
});

app.MapGet("/api/analysis/results", async (DateTimeOffset? from, DateTimeOffset? to, string? direction,
    string? outcome, string? timeframe, ServerOrderStore store, CancellationToken token) =>
{
    if (from is null || to is null || to <= from)
        return Results.BadRequest(new { error = "بازه تاریخ معتبر نیست." });
    if (to.Value - from.Value > TimeSpan.FromDays(3660))
        return Results.BadRequest(new { error = "بازه گزارش نمی‌تواند بیشتر از ده سال باشد." });

    var history = await store.GetHistoryRangeAsync(from.Value.UtcDateTime, to.Value.UtcDateTime, 50000, token);
    var filtered = history.Where(item =>
        (string.IsNullOrWhiteSpace(direction) || direction == "All" || item.Signal.Direction == direction) &&
        (string.IsNullOrWhiteSpace(outcome) || outcome == "All" || item.Signal.Outcome == outcome ||
            outcome == "ExpiredNearEntry" && item.Signal.Outcome == "Expired" && item.Signal.ExpireReason == "TargetAfterActivation") &&
        (string.IsNullOrWhiteSpace(timeframe) || timeframe == "All" || item.Signal.Timeframe == timeframe)).ToArray();
    var signals = filtered.Select(item => item.Signal).ToArray();
    var entered = signals.Count(signal => signal.SubmittedAtUtc is not null || signal.FilledAtUtc is not null ||
        signal.Outcome is "Target" or "StopLoss" or "RiskFree");
    var details = filtered
        .Where(item => item.Signal.Outcome is "Target" or "StopLoss" or "RiskFree" ||
            item.Signal.Outcome == "Expired" && item.Signal.ExpireReason == "TargetAfterActivation")
        .OrderByDescending(item => item.Signal.CompletedAtUtc)
        .Select(item => new
        {
            item.Signal.Id, item.Signal.Symbol, item.Signal.Direction, item.Signal.Outcome, item.Signal.ExpireReason,
            item.Signal.CreatedAtUtc, item.Signal.CompletedAtUtc, item.Signal.EntryPrice, item.Signal.TakeProfit,
            item.Signal.StopLoss, item.Signal.AverageFillPrice, item.Signal.Leverage, item.Signal.PositionSizeUsdt,
            item.Signal.Timeframe, item.Signal.ChartMode, item.HasImage,
            item.Signal.MarketRegime, item.Signal.AnalysisSummary, item.Signal.FailureReason,
            imageUrl = item.HasImage ? $"/api/analysis/results/{item.Signal.Id}/image" : null
        });
    return Results.Ok(new
    {
        from, to,
        summary = new
        {
            total = signals.Length,
            entered,
            expired = signals.Count(signal => signal.Outcome == "Expired"),
            target = signals.Count(signal => signal.Outcome == "Target"),
            stopLoss = signals.Count(signal => signal.Outcome == "StopLoss"),
            riskFree = signals.Count(signal => signal.Outcome == "RiskFree")
        },
        byDirection = signals.Where(signal => signal.Direction is "Long" or "Short")
            .GroupBy(signal => signal.Direction).Select(group => new { key = group.Key, count = group.Count() }),
        byTimeframe = signals.Where(signal => !string.IsNullOrWhiteSpace(signal.Timeframe))
            .GroupBy(signal => signal.Timeframe).OrderBy(group => group.Key)
            .Select(group => new { key = group.Key, count = group.Count() }),
        byOutcome = signals.Where(signal => !string.IsNullOrWhiteSpace(signal.Outcome))
            .GroupBy(signal => signal.Outcome == "Expired" && signal.ExpireReason == "TargetAfterActivation"
                ? "ExpiredNearEntry" : signal.Outcome!)
            .Select(group => new { key = group.Key, count = group.Count() }),
        byFailureReason = signals.Where(signal => signal.Outcome == "StopLoss" &&
                !string.IsNullOrWhiteSpace(signal.FailureReason))
            .GroupBy(signal => signal.FailureReason!).OrderByDescending(group => group.Count())
            .Select(group => new { key = group.Key, count = group.Count() }),
        details
    });
});
app.MapGet("/api/analysis/results/{id:guid}/image", async (Guid id, ServerOrderStore store, CancellationToken token) =>
{
    var image = await store.GetHistoryImageAsync(id, token);
    return image is null ? Results.NotFound() : Results.File(image, "image/png");
});

app.MapGet("/api/analysis/proposals", async (HttpRequest request, SignalBatchService batches, CancellationToken ct) => {
    PhoenixSessionAuth.TryGetIdentity(request,out var identity);
    var items=await batches.PendingAsync(identity.Username ?? "",ct);
    return Results.Ok(items.Select(x=>new {x.Key,x.Caption,x.CreatedAt,x.State,x.Error,evidence=x.Analysis.Prediction.Evidence,symbol=x.Candidate.Symbol,
        image="/api/analysis/proposals/"+x.Key+"/image"}));
});
app.MapGet("/api/analysis/proposals/{key}/image", async (string key,HttpRequest request,SignalBatchService batches,CancellationToken ct)=>{
    PhoenixSessionAuth.TryGetIdentity(request,out var identity);
    var item=(await batches.PendingAsync(identity.Username ?? "",ct)).SingleOrDefault(x=>x.Key==key);
    return item is null?Results.NotFound():Results.File(item.Image,"image/png");
});
app.MapPost("/api/analysis/proposals/restore", async (HttpRequest request, SignalBatchService batches) => {
    PhoenixSessionAuth.TryGetIdentity(request, out var identity);
    return Results.Ok(new { restored = await batches.RestoreRemovedProposalsAsync(identity.Username ?? "", CancellationToken.None) });
});
app.MapPost("/api/analysis/proposals/remove", async (RemoveProposalsRequest removal, HttpRequest request, SignalBatchService batches) => {
    PhoenixSessionAuth.TryGetIdentity(request, out var identity);
    if (removal.Keys is null || removal.Keys.Length > 100 || removal.Keys.Any(string.IsNullOrWhiteSpace))
        return Results.BadRequest(new { error = "فهرست پیشنهادها معتبر نیست." });
    var removed = await batches.RemoveProposalsAsync(identity.Username ?? "", removal.Keys, CancellationToken.None);
    return Results.Ok(new { removed });
});
app.MapPost("/api/analysis/proposals/{key}/decision", async (string key,InAppDecisionRequest decision,HttpRequest request,SignalBatchService batches,CancellationToken ct)=>{
    PhoenixSessionAuth.TryGetIdentity(request,out var identity);
    var error=await batches.DecideInAppAsync(identity.Username ?? "",key,decision.Action,decision.Reason,CancellationToken.None);
    return error is null?Results.Ok():Results.Conflict(new {error});
});
app.MapGet("/api/analysis/signal-batch", (SignalBatchService batches) => Results.Ok(batches.Status));
app.MapGet("/api/analysis/learning", async (SignalLearningService learning, HttpContext context, CancellationToken token) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var snapshot = await learning.GetSnapshotAsync(token);
    return Results.Ok(new
    {
        snapshot.RefreshedAtUtc,
        entrySamples = SignalPatternIdentity.Unique(snapshot.Patterns).Length,
        proposalSamples = SignalPatternIdentity.Unique(snapshot.ProposalPatterns).Length,
        entryModelValidated = snapshot.OutcomeModel is not null,
        proposalModelValidated = snapshot.ProposalOutcomeModel is not null,
        minimumSamples = 100,
        condition = "Entered signals ending in Target or StopLoss only; not all proposed signals.",
        proposalValidationCount = snapshot.ProposalOutcomeModel?.ValidationCount ?? 0,
        proposalBrier = snapshot.ProposalOutcomeModel?.ValidationBrier,
        proposalBaselineBrier = snapshot.ProposalOutcomeModel?.BaselineBrier
    });
});
app.MapGet("/api/analysis/shadow-signals", async (ShadowSignalRuntime shadow, CancellationToken token) =>
{
    var signals = (await shadow.Store.GetAllAsync(token)).OrderByDescending(x => x.CreatedAtUtc).ToArray();
    return Results.Ok(new
    {
        summary = new
        {
            total = signals.Length,
            waitingEntry = signals.Count(x => x.Status == "Pending"),
            entered = signals.Count(x => x.Status == "Filled"),
            target = signals.Count(x => x.Outcome == "Target"),
            stopLoss = signals.Count(x => x.Outcome == "StopLoss")
        },
        signals = signals.Take(100).Select(x => new
        {
            x.Id, x.Symbol, x.Direction, x.Status, x.Outcome, x.CreatedAtUtc, x.CompletedAtUtc,
            x.EntryPrice, x.TakeProfit, x.StopLoss, x.LastPrice, x.Timeframe,
            targetProbability = x.TargetOutcomePercent,
            stopProbability = x.StopOutcomePercent,
            imageUrl = $"/api/analysis/shadow-signals/{x.Id}/image"
        })
    });
});
app.MapGet("/api/analysis/shadow-signals/{id:guid}/image", async (Guid id,
    ShadowSignalRuntime shadow, CancellationToken token) =>
{
    var image = await shadow.Store.GetHistoryImageAsync(id, token);
    return image is null ? Results.NotFound() : Results.File(image, "image/png");
});
app.MapGet("/api/analysis/evaluation/export", async (DateTimeOffset? from, DateTimeOffset? to,
    string? format, int? offset, int? limit,
    HttpContext context, ServerOrderStore store, ShadowSignalRuntime shadow,
    Strategy2Runtime strategy2, CancellationToken token) =>
{
    if (!AnalysisSessionAuth.TryGetIdentity(context.Request, out var identity) || !identity.IsAdmin)
        return Results.StatusCode(403);
    if (format is not (null or "zip" or "json"))
        return Results.BadRequest(new { error = "نوع خروجی معتبر نیست." });
    var end = to?.UtcDateTime ?? DateTime.UtcNow;
    var start = from?.UtcDateTime ?? end.AddDays(-365);
    if (end <= start || end - start > TimeSpan.FromDays(3650))
        return Results.BadRequest(new { error = "بازه باید معتبر و حداکثر ده سال باشد." });
    if (!await SignalEvaluationExport.Gate.WaitAsync(0, token))
        return Results.Json(new { error = "یک خروجی در حال آماده‌سازی است؛ کمی بعد دوباره تلاش کنید." }, statusCode: 429);
    try
    {
        var sources = new List<(string Source, IReadOnlyList<SignalHistoryItem> Rows)>();
        foreach (var (name, source) in new[] { ("main", store), ("observation", shadow.Store), ("strategy2", strategy2.Store) })
        {
            var rows = await source.GetHistoryRangeAsync(start, end, SignalEvaluationExport.MaxRowsPerSource + 1, token);
            if (rows.Count > SignalEvaluationExport.MaxRowsPerSource)
                return Results.BadRequest(new { error = "بیش از ۲۰۰۰ سیگنال در یکی از بخش‌ها وجود دارد؛ بازه تاریخ را کوتاه‌تر کنید." });
            sources.Add((name, rows));
        }
        context.Response.Headers.CacheControl = "no-store";
        if (format == "json")
            return Results.Json(SignalEvaluationExport.Page(start, end, sources, offset ?? 0, limit ?? 100, token));
        var zip = SignalEvaluationExport.Build(start, end, sources, token);
        return Results.File(zip, "application/zip", $"phoenix-evaluation-{start:yyyyMMdd}-{end:yyyyMMdd}.zip");
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    finally { SignalEvaluationExport.Gate.Release(); }
});
app.MapGet("/api/analysis/reviews/export", async (DateTimeOffset? from, DateTimeOffset? to,
    HttpRequest request, ReviewArchiveStore reviews, CancellationToken token) =>
{
    if (!AnalysisSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin)
        return Results.StatusCode(403);
    if (from is null || to is null) return Results.BadRequest(new { error = "بازه تاریخ را انتخاب کنید." });
    try
    {
        var zip = await reviews.ExportAsync(from.Value.UtcDateTime, to.Value.UtcDateTime, token);
        return Results.File(zip, "application/zip", $"phoenix-reviews-{from:yyyyMMdd}-{to:yyyyMMdd}.zip");
    }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapPost("/api/analysis/signal-batch", async (StartSignalBatchRequest request, HttpRequest httpRequest,
    SignalBatchService batches, SignalLearningService learning, CancellationToken token) =>
{
    if (!request.TimedMode && request.Count is < 1 or > 200)
        return Results.BadRequest(new { error = "تعداد باید بین ۱ تا ۲۰۰ باشد." });
    if (request.TimedMode && request.DurationMinutes is not (5 or 10 or 20 or 30 or 45 or 60))
        return Results.BadRequest(new { error = "مدت جست‌وجوی زمان‌دار باید ۵، ۱۰، ۲۰، ۳۰، ۴۵ یا ۶۰ دقیقه باشد." });
    if (request.PositionSizeUsdt <= 0) return Results.BadRequest(new { error = "مقدار ورودی باید بیشتر از صفر باشد." });
    var directionFilter = string.IsNullOrWhiteSpace(request.DirectionFilter) ? "All" : request.DirectionFilter;
    if (directionFilter is not ("All" or "Long" or "Short")) return Results.BadRequest(new { error = "فیلتر جهت معتبر نیست." });
    var chartFilter = string.IsNullOrWhiteSpace(request.ChartFilter) ? "All" : request.ChartFilter;
    if (chartFilter is not ("All" or "Candles" or "Line")) return Results.BadRequest(new { error = "نوع نمودار معتبر نیست." });
    var timeframeFilter = string.IsNullOrWhiteSpace(request.TimeframeFilter) ? "All" : request.TimeframeFilter;
    if (timeframeFilter is not ("All" or "1" or "5" or "15" or "60" or "240"))
        return Results.BadRequest(new { error = "تایم‌فریم معتبر نیست." });
    PhoenixSessionAuth.TryGetIdentity(httpRequest, out var identity);
    return batches.Start(request.Count, request.PositionSizeUsdt, directionFilter, chartFilter,
            timeframeFilter, 0m, request.TimedMode, request.DurationMinutes,
            identity.Username, out var error)
        ? Results.Accepted(value: batches.Status)
        : Results.Conflict(new { error });
});
app.MapPost("/api/analysis/signal-batch/stop", (SignalBatchService batches, string? runId) =>
    batches.Stop(out var error, runId)
        ? Results.Ok(batches.Status)
        : Results.Conflict(new { error }));

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
        var analysis = await analyzer.AnalyzeAsync(ElliottWaveAnalyzer.ClosedCandles(candles, interval ?? "60", DateTimeOffset.UtcNow), token, Math.Clamp(depth ?? 5, 2, 20), deviation ?? 0.6m);
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

app.MapPost("/api/analysis/signals/confirm", async (ConfirmSignalRequest request, HttpRequest httpRequest,
    SignalSubmissionService submission, CancellationToken token) =>
{
    if (!request.Confirmed)
        return Results.BadRequest(new { error = "ثبت سیگنال نیازمند تأیید صریح است." });
    PhoenixSessionAuth.TryGetIdentity(httpRequest, out var identity);
    return await submission.SubmitAsync(request.Signal, token, identity.Username);
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

ManualSignals.Map(app);
TimeframeRiskEndpoints.Map(app);

app.MapPost("/api/signals", async (SignalRequest request, HttpRequest httpRequest,
    SignalSubmissionService submission, CancellationToken token) =>
{
    var panelKey = Environment.GetEnvironmentVariable("PHOENIX_PANEL_KEY");
    if (!string.IsNullOrWhiteSpace(panelKey) && httpRequest.Headers["X-Phoenix-Key"] != panelKey)
        return Results.Json(new { error = "کلید ورود پنل صحیح نیست." }, statusCode: StatusCodes.Status401Unauthorized);

    PhoenixSessionAuth.TryGetIdentity(httpRequest, out var identity);
    return await submission.SubmitAsync(request, token, identity.Username);
});

app.MapPost("/api/signals/cancel-selected",async (CancelSelectedRequest input,HttpRequest request,SignalCancellationService cancellation,CancellationToken token)=>
{
    if(!PhoenixSessionAuth.TryGetIdentity(request,out var user)||!user.IsAdmin)return Results.StatusCode(403);
    if(input.Ids is null||input.Ids.Length is <1 or >100)return Results.BadRequest(new{error="بین ۱ تا ۱۰۰ سیگنال انتخاب کن."});
    var items=await cancellation.CancelSelectedAsync(input.Ids,token);return Results.Ok(new{items,cancelled=items.Count(x=>x.Cancelled)});
});
app.MapGet("/api/signals/{id:guid}/live-chart",async (Guid id,ServerOrderStore store,BybitDemoClient bybit,HttpResponse response,CancellationToken token)=>
{
    response.Headers.CacheControl="no-store";
    var signal=await store.FindSignalAsync(id,token);if(signal is null)return Results.NotFound();
    var interval=signal.Timeframe??"60";
    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
    try
    {
        var candlesTask=bybit.GetKlinesAsync(signal.Symbol,interval=="3M"?"M":interval,150,timeout.Token);
        var tickerTask=bybit.GetLastPriceAsync(signal.Symbol,timeout.Token);await Task.WhenAll(candlesTask,tickerTask);
        return Results.Ok(new{signal.Id,signal.Symbol,signal.Direction,interval,updatedAtUtc=DateTime.UtcNow,
            lastPrice=tickerTask.Result.LastPrice,candles=interval=="3M"?ManualSignals.AggregateQuarters(candlesTask.Result):candlesTask.Result,
            levels=new[]{new{label="CEILING",price=signal.Ceiling},new{label="FLOOR",price=signal.Floor},
                new{label="ENTRY",price=signal.EntryPrice},new{label="TP",price=signal.TakeProfit},new{label="SL",price=signal.StopLoss},
                new{label="RF",price=signal.RiskFreePrice??0m},new{label="RF1 SL",price=signal.EarlyStopLimit??0m},new{label="RF2 SL",price=signal.StopLoss2??0m}}.Where(x=>x.price>0)});
    }
    catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
    catch{return Results.Json(new{error="دریافت نمودار زنده ناموفق بود؛ آخرین زمان دریافت را بررسی کن."},statusCode:503);}
});

app.MapDelete("/api/signals/{id:guid}", async (Guid id, SignalCancellationService cancellation,
    CancellationToken token) =>
{
    try
    {
        var result = await cancellation.CancelAsync(id, token);
        if (result.Cancelled) return Results.Ok(new { message = result.Message });
        return Results.Conflict(new { error = result.Message });
    }
    catch (Exception exception) { return Results.BadRequest(new { error = "لغو انجام نشد: " + exception.Message }); }
});

app.MapPost("/api/signals/cancel-pending/{direction}", async (string direction, HttpRequest request,
    ServerOrderStore store, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin)
        return Results.Json(new { error = "برای لغو گروهی باید با حساب مدیر وارد شوید." }, statusCode: 403);
    if (direction is not ("All" or "Long" or "Short")) return Results.BadRequest(new { error = "جهت نامعتبر است." });
    await store.ExecutionGate.WaitAsync(token);
    try { return Results.Ok(new { cancelled = await store.CancelPendingAsync(direction, token) }); }
    finally { store.ExecutionGate.Release(); }
});
app.MapGet("/api/positions/live", async (HttpContext context, BybitDemoClient client,
    ServerOrderStore store, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(context.Request, out var identity) || !identity.IsAdmin)
        return Results.StatusCode(403);
    context.Response.Headers.CacheControl = "no-store";
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
    timeout.CancelAfter(TimeSpan.FromSeconds(8));
    try
    {
        var positions = await client.GetOpenPositionsAsync(timeout.Token);
        var signals = await store.GetAllAsync(timeout.Token);
        var items = positions.Select(p =>
        {
            var matches = signals.Where(s => s.Symbol.Equals(p.Symbol, StringComparison.OrdinalIgnoreCase)
                && s.Direction == (p.Side == "Buy" ? "Long" : "Short")
                && s.FilledAtUtc is not null && s.CompletedAtUtc is null).ToArray();
            // Bybit aggregates each symbol/side. Do not attribute an aggregate to an arbitrary signal.
            var signal = matches.Length == 1 ? matches[0] : null;
            return new { position = p, signalId = signal?.Id, timeframe = signal?.Timeframe,
                protectionStage = signal?.ProtectionStage, riskFreeReached = signal?.RiskFreeReachedAtUtc is not null,
                firstProtection = signal?.EarlyProtectionAtUtc is not null, closing = matches.Any(s=>s.Status=="Closing"), matchedSignals = matches.Length };
        }).ToArray();
        return Results.Ok(new { updatedAtUtc = DateTime.UtcNow, items });
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
    catch
    {
        return Results.Json(new { error = "دریافت معاملات باز از بای‌بیت ناموفق بود؛ دوباره تلاش کن." }, statusCode: 503);
    }
});

app.MapPost("/api/positions/close-preview", async (HttpRequest request, BulkPositionService bulk, CancellationToken token,string? symbol,string? side,int? positionIndex) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin) return Results.StatusCode(403);
    try { return Results.Ok(await bulk.PreviewAsync(token,symbol,side,positionIndex,identity.Username)); }
    catch { return Results.BadRequest(new { error = "دریافت پوزیشن‌های صرافی ناموفق بود؛ هیچ تغییری انجام نشد." }); }
});
app.MapPost("/api/positions/close-all/{previewId:guid}", async (Guid previewId, HttpRequest request,
    BulkPositionService bulk) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin) return Results.StatusCode(403);
    // Once explicitly confirmed, disconnecting the browser must not interrupt reconciliation.
    try { return Results.Ok(new { items = await bulk.CloseAsync(previewId, CancellationToken.None), entriesPaused = false }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch { return Results.Problem("عملیات کامل نشد؛ وضعیت صرافی و توقف ورودها را بررسی کنید."); }
});
app.MapGet("/api/positions/entry-pause", (ServerOrderStore store) => Results.Ok(new { paused = store.EntriesPaused }));
app.MapPost("/api/positions/resume-entries", async (HttpRequest request, ServerOrderStore store, CancellationToken token) =>
{
    if (!PhoenixSessionAuth.TryGetIdentity(request, out var identity) || !identity.IsAdmin) return Results.StatusCode(403);
    await store.ExecutionGate.WaitAsync(token);
    try
    {
        if ((await store.GetAllAsync(token)).Any(x => x.Status == "Closing"))
            return Results.Conflict(new { error = "پوزیشن در انتظار تأیید بسته‌شدن وجود دارد؛ ابتدا وضعیت آن را بررسی کنید." });
        await store.SetEntriesPausedAsync(false, token);
        return Results.Ok(new { paused = false });
    }
    finally { store.ExecutionGate.Release(); }
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

    public sealed record CreateUserRequest(string Username, string Password, string ConfirmPassword, bool ViewerOnly)
    {
        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Username) || Username.Length is < 3 or > 32 ||
                Username.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
                return "نام کاربری باید ۳ تا ۳۲ کاراکتر و فقط شامل حروف انگلیسی، عدد، خط تیره، زیرخط یا نقطه باشد.";
            if (Password.Length is < 6 or > 128)
                return "رمز عبور باید حداقل ۶ و حداکثر ۱۲۸ کاراکتر باشد.";
            if (Password != ConfirmPassword) return "تکرار رمز عبور یکسان نیست.";
            return null;
        }
    }

    public sealed record LoginRequest(string Username, string Password);
    public sealed record TelegramAccessRequest(long UserId, string DisplayName, string? Username)
    {
        public string? Validate()
        {
            if (UserId <= 0) return "شناسه عددی تلگرام معتبر نیست.";
            if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Trim().Length > 80)
                return "نام کاربر باید بین ۱ تا ۸۰ کاراکتر باشد.";
            if (!string.IsNullOrWhiteSpace(Username) &&
                Username.Trim().TrimStart('@').Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                return "نام کاربری تلگرام معتبر نیست.";
            return null;
        }
    }
    public sealed record TelegramAccessStateRequest(bool Enabled);
    public sealed record ConfirmSignalRequest(bool Confirmed, SignalRequest Signal);
}

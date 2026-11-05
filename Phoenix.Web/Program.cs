using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;
using Phoenix.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.AddRateLimiter(options => {
    options.AddPolicy("account-login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = async (context, token) => { context.HttpContext.Response.StatusCode = 429; context.HttpContext.Response.Headers.RetryAfter = "60"; await context.HttpContext.Response.WriteAsJsonAsync(new { error = "تلاش‌های ورود زیاد شده؛ یک دقیقه بعد دوباره امتحان کنید." }, token); };
});
builder.Services.AddSingleton<CommodityQuotes>();
builder.Services.AddHttpClient("MarketRisk", client => {
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixRiskObserver/1.0");
});
builder.Services.AddSingleton<MarketRiskObserver>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MarketRiskObserver>());
builder.Services.AddHttpClient("CommodityQuotes", client => {
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 PhoenixMarket/1.0");
});
builder.Services.AddSingleton<MobilePushService>();
builder.Services.AddSingleton<MobileSignalFeed>();
builder.Services.AddSingleton<BybitWalletRecovery>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BybitWalletRecovery>());
builder.Services.AddHostedService<MobileSignalWorker>();
builder.Services.AddHostedService<MobilePushDeliveryWorker>();
builder.Services.AddHostedService<MobileEntryConfirmationWorker>();
builder.Services.AddHostedService<Strategy2JournalWorker>();
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
builder.Services.AddHostedService<BybitConnectionWorker>();
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
// Correlate failures without recording cookies, request bodies or credentials.
var connectionInstance = Guid.NewGuid().ToString("N")[..12];
var connectionStarted = DateTime.UtcNow;
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
    var requestId = Guid.NewGuid().ToString("N");
    var started = System.Diagnostics.Stopwatch.StartNew();
    context.Response.Headers["X-Phoenix-Request-Id"] = requestId;
    context.Response.Headers["X-Phoenix-Instance"] = connectionInstance;
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogWarning("Phoenix client disconnected {Path} request {RequestId} after {Elapsed}ms instance {Instance}",
            context.Request.Path, requestId, started.ElapsedMilliseconds, connectionInstance);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Phoenix API failure {Path} request {RequestId} instance {Instance}",
            context.Request.Path, requestId, connectionInstance);
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "خطای داخلی سرور؛ کد پیگیری را برای بررسی نگه دارید.", requestId });
    }
    finally
    {
        if (context.Response.StatusCode >= 500 || started.Elapsed > TimeSpan.FromSeconds(5))
            app.Logger.LogWarning("Phoenix API {Path} status {Status} request {RequestId} duration {Elapsed}ms instance {Instance}",
                context.Request.Path, context.Response.StatusCode, requestId, started.ElapsedMilliseconds, connectionInstance);
    }
});
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var users = context.RequestServices.GetRequiredService<PhoenixUserStore>();
    var path = context.Request.Path.Value;
    if (path is "/" or "/index.html")
    {
        // Avoid a second network redirect through the protected home page before login.
        context.Response.Redirect(PhoenixSessionAuth.TryGetIdentity(context.Request,out _) ? "/phoenix" : "/login");
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
// Only compress public-shaped assets and the read-only event history, never authentication responses.
app.UseWhen(context => context.Request.Path.Value is { } path &&
    (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
     path == "/api/mobile/history"), branch => branch.UseResponseCompression());
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var extension = Path.GetExtension(context.File.Name).ToLowerInvariant();
        if (extension is ".js" or ".css" or ".woff2" or ".png" or ".svg" or ".jpg" or ".webp" or ".mp4")
            context.Context.Response.Headers.CacheControl = context.Context.Request.Query.ContainsKey("v")
                ? "private, max-age=3600" : "private, max-age=0, must-revalidate";
    }
});

app.MapGet("/", () => Results.Redirect("/phoenix"));
app.MapGet("/index.html", () => Results.Redirect("/phoenix"));
app.MapGet("/phoenix", () => Results.File(Path.Combine(app.Environment.WebRootPath, "phoenix.html"), "text/html; charset=utf-8"));
app.MapMobileEndpoints();

app.MapGet("/api/market-risk", (MarketRiskObserver observer, HttpResponse response) => {
    response.Headers.CacheControl = "no-store";
    return Results.Ok(observer.Snapshot());
});

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
app.MapGet("/api/market/commodities", async (CommodityQuotes quotes, HttpResponse response, CancellationToken token) => {
    response.Headers.CacheControl = "no-store";
    return Results.Ok(new { items = await quotes.ReadAsync(token) });
});
app.MapGet("/api/market/commodities/history", async (CommodityQuotes quotes, HttpResponse response, CancellationToken token) => {
    response.Headers.CacheControl = "private, max-age=60";
    return Results.Ok(new { items = await quotes.ReadHistoryAsync(token) });
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

app.MapGet("/api/status", (ServerState state, BybitDemoOptions options, TelegramNotifier telegram) => {
    var connection=state.Read(options.HasCredentials);
    return Results.Ok(new {
        publicApiConnected=connection.PublicApi.Status=="connected",
        demoAuthenticated=connection.Account.Status=="connected",
        tradingEnvironment=options.EnvironmentName, credentialsConfigured=options.HasCredentials,
        lastPrice=state.LastPrice, lastUpdatedUtc=state.LastUpdatedUtc,
        error=state.Error, connection,
        serverTimeUtc=DateTime.UtcNow,
        panelLocked=!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHOENIX_PANEL_KEY")),
        tradingEnabled=DemoOrderWorker.IsTradingEnabled(options),telegramConfigured=telegram.IsConfigured,
        aiConfigured=!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
    });
});

app.MapPost("/api/ai/parse-signal", async (AiSignalTextRequest request, AiSignalParser parser,
    CancellationToken token) =>
{
    var result = await parser.ParseAsync(request.Text, token);
    return result.Complete ? Results.Ok(result) : Results.BadRequest(result);
});

// No leverage refresh, similarity refresh or exchange calls for a UI counter.
app.MapGet("/api/signals/active-count", async (ServerOrderStore store,HttpResponse response,CancellationToken token) => {
    response.Headers.CacheControl="no-store";
    var signals=await store.GetAllAsync(token);
    return Results.Ok(new{count=signals.Count(s=>s.CompletedAtUtc is null && s.TargetReachedAtUtc is null &&
        s.StopLossReachedAtUtc is null && s.RiskFreeClosedAtUtc is null &&
        s.Status is not ("Error" or "Rejected" or "Cancelled" or "Deactivated" or "Expired"))});
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
        x.TechnicalFeatures is not null && x.SimilarityEvidence?.ComparisonVersion != TechnicalResemblance.Version))
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
        var signals = await strategy2.Store.GetAllAsync(token);
        var recoveryRequired = signals.Count(x => x.Status == "RecoveryRequired");
        return Results.Ok(new { enabled = true, authenticated = status.Authenticated, availableBalance = balance,
            entriesPaused = recoveryRequired > 0, recoveryRequired });
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
app.MapGet("/api/analysis/results/{id:guid}/image", async (Guid id, string? strategy, ServerOrderStore store, Strategy2Runtime strategy2, CancellationToken token) =>
{
    if (strategy is not (null or "main" or "strategy2")) return Results.BadRequest();
    var image = await (strategy == "strategy2" ? strategy2.Store : store).GetHistoryImageAsync(id, token);
    return image is null ? Results.NotFound() : Results.File(image, "image/png");
});

app.MapGet("/api/analysis/proposals", async (HttpRequest request, SignalBatchService batches, CancellationToken ct) => {
    PhoenixSessionAuth.TryGetIdentity(request,out var identity);
    var items=await batches.PendingAsync(identity.Username ?? "",ct);
    return Results.Ok(items.Select(x=>new {x.Key,x.Caption,x.CreatedAt,x.State,x.Error,evidence=x.Analysis.Prediction.Evidence,symbol=x.Candidate.Symbol,
        image="/api/analysis/proposals/"+x.Key+"/image"}));
});
app.MapGet("/api/analysis/proposals/{key}/image", async (string key,HttpRequest request,HttpResponse response,SignalBatchService batches,CancellationToken ct)=>{
    PhoenixSessionAuth.TryGetIdentity(request,out var identity);
    var image=await batches.PendingImageAsync(identity.Username ?? "",key,ct);
    if(image is null)return Results.NotFound();
    var etag="\""+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image))+"\"";
    response.Headers.CacheControl="private, max-age=0, must-revalidate";
    response.Headers.ETag=etag;
    response.Headers["Vary"]="Cookie";
    if(request.Headers.IfNoneMatch.Any(v=>v is not null && v.Split(',').Any(t=>t.Trim()==etag)))
        return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.File(image,"image/png");
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
app.MapPost("/api/analysis/proposals/{key}/decision", async (s…7705 tokens truncated…u0646ام کاربر باید بین ۱ تا ۸۰ کاراکتر باشد.";
            if (!string.IsNullOrWhiteSpace(Username) &&
                Username.Trim().TrimStart('@').Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                return "نام کاربری تلگرام معتبر نیست.";
            return null;
        }
    }
    public sealed record TelegramAccessStateRequest(bool Enabled);
    public sealed record ConfirmSignalRequest(bool Confirmed, SignalRequest Signal);
}

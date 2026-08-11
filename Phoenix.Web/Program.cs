using System.Collections.Concurrent;
using Phoenix.Engine.Exchanges.Bybit;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(BybitDemoOptions.FromEnvironment());
builder.Services.AddSingleton<BybitDemoClient>();
builder.Services.AddSingleton<ServerState>();
builder.Services.AddHostedService<BybitMonitor>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "phoenix-web" }));
app.MapGet("/api/status", (ServerState state, BybitDemoOptions options) => Results.Ok(new
{
    publicApiConnected = state.PublicApiConnected,
    demoAuthenticated = state.DemoAuthenticated,
    credentialsConfigured = options.HasCredentials,
    lastPrice = state.LastPrice,
    lastUpdatedUtc = state.LastUpdatedUtc,
    error = state.Error,
    panelLocked = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHOENIX_PANEL_KEY")),
    tradingEnabled = false
}));

app.MapGet("/api/signals", (ServerState state) => Results.Ok(state.Signals.OrderByDescending(x => x.CreatedAtUtc)));

app.MapPost("/api/signals", (SignalRequest request, ServerState state, HttpRequest httpRequest) =>
{
    var panelKey = Environment.GetEnvironmentVariable("PHOENIX_PANEL_KEY");
    if (!string.IsNullOrWhiteSpace(panelKey) && httpRequest.Headers["X-Phoenix-Key"] != panelKey)
        return Results.Json(new { error = "کلید ورود پنل صحیح نیست." }, statusCode: StatusCodes.Status401Unauthorized);

    var error = request.Validate();
    if (error is not null)
        return Results.BadRequest(new { error });

    var signal = new ServerSignal(
        Guid.NewGuid(), request.Symbol.Trim().ToUpperInvariant(), request.Direction,
        request.Ceiling, request.Floor, request.PositionSizeUsdt, DateTime.UtcNow, "در انتظار");
    state.Signals.Add(signal);
    return Results.Created($"/api/signals/{signal.Id}", signal);
});

app.MapFallbackToFile("index.html");
app.Run();

sealed class ServerState
{
    public bool PublicApiConnected { get; set; }
    public bool DemoAuthenticated { get; set; }
    public decimal? LastPrice { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
    public string? Error { get; set; }
    public ConcurrentBag<ServerSignal> Signals { get; } = [];
}

sealed class BybitMonitor(BybitDemoClient client, BybitDemoOptions options, ServerState state, ILogger<BybitMonitor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ticker = await client.GetLastPriceAsync("BTCUSDT", stoppingToken);
                state.LastPrice = ticker.LastPrice;
                state.LastUpdatedUtc = DateTime.UtcNow;
                state.PublicApiConnected = true;
                state.Error = null;

                if (options.HasCredentials && !state.DemoAuthenticated)
                {
                    var status = await client.CheckConnectionAsync(stoppingToken);
                    state.DemoAuthenticated = status.Authenticated;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                state.PublicApiConnected = false;
                state.DemoAuthenticated = false;
                state.Error = exception.Message;
                logger.LogWarning(exception, "Bybit connectivity check failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}

sealed record SignalRequest(string Symbol, string Direction, decimal Ceiling, decimal Floor, decimal PositionSizeUsdt)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Symbol) || Symbol.Any(c => !char.IsAsciiLetterOrDigit(c)))
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

sealed record ServerSignal(Guid Id, string Symbol, string Direction, decimal Ceiling, decimal Floor,
    decimal PositionSizeUsdt, DateTime CreatedAtUtc, string Status);

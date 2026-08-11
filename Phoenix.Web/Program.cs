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
builder.Services.AddSingleton<StrategyCalculator>();
builder.Services.AddHostedService<DemoOrderWorker>();

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
    tradingEnabled = DemoOrderWorker.IsTradingEnabled(options)
}));

app.MapGet("/api/signals", async (ServerOrderStore store, CancellationToken token) =>
    Results.Ok((await store.GetAllAsync(token)).OrderByDescending(x => x.CreatedAtUtc)));

app.MapPost("/api/signals", async (SignalRequest request, HttpRequest httpRequest, ServerOrderStore store,
    StrategyCalculator calculator, BybitDemoClient bybit, CancellationToken token) =>
{
    var panelKey = Environment.GetEnvironmentVariable("PHOENIX_PANEL_KEY");
    if (!string.IsNullOrWhiteSpace(panelKey) && httpRequest.Headers["X-Phoenix-Key"] != panelKey)
        return Results.Json(new { error = "کلید ورود پنل صحیح نیست." }, statusCode: StatusCodes.Status401Unauthorized);

    var error = request.Validate();
    if (error is not null)
        return Results.BadRequest(new { error });

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
        var position = new ExecutionManager().PreparePosition(signal)
            ?? throw new InvalidOperationException("ساخت موقعیت برنامه‌ریزی‌شده ناموفق بود.");
        var rules = await bybit.GetInstrumentRulesAsync(signal.Symbol, token);
        var preview = BybitOrderPreviewBuilder.Build(signal.Symbol, position, rules);
        var queued = ServerSignal.FromPreview(signal, preview);
        await store.AddAsync(queued, token);
        return Results.Created($"/api/signals/{queued.Id}", queued);
    }
    catch (Exception exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
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
}

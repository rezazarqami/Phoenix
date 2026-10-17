using System.Globalization;
using System.Text.Json;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Services;

namespace Phoenix.Web;

// Read-only proposal metadata. Never used for sizing, approval, or order submission.
public sealed class ProposalLeverageCache : BackgroundService
{
    private sealed record Snapshot(DateTime UpdatedAtUtc, Dictionary<string, BybitInstrumentRules> Rules);
    private readonly BybitDemoClient client;
    private readonly ILogger<ProposalLeverageCache> logger;
    private readonly string path;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Snapshot snapshot = new(DateTime.MinValue, new(StringComparer.OrdinalIgnoreCase));

    public ProposalLeverageCache(BybitDemoClient client, ILogger<ProposalLeverageCache> logger,
        string? cachePath = null, TimeProvider? timeProvider = null)
    {
        this.client = client; this.logger = logger; clock = timeProvider ?? TimeProvider.System;
        path = cachePath ?? Path.Combine(Path.GetDirectoryName(
            Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH") ??
            Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json"))!, "proposal-leverage-cache.json");
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
                if (loaded is not null && loaded.UpdatedAtUtc <= clock.GetUtcNow().UtcDateTime &&
                    loaded.Rules.Count > 0 && loaded.Rules.Values.All(Valid))
                    snapshot = loaded with { Rules = new(loaded.Rules, StringComparer.OrdinalIgnoreCase) };
            }
        }
        catch (Exception exception) { logger.LogWarning(exception, "Could not load informational leverage cache"); }
    }

    private static bool Valid(BybitInstrumentRules r) => r.MaximumLeverage > 0 && r.MinimumLeverage > 0 &&
        r.MinimumLeverage <= r.MaximumLeverage && r.LeverageStep > 0;

    public async Task RefreshAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (clock.GetUtcNow().UtcDateTime - Volatile.Read(ref snapshot).UpdatedAtUtc < TimeSpan.FromHours(24)) return;
            var fetched = await client.GetLinearLeverageCatalogAsync(token);
            var next = new Snapshot(clock.GetUtcNow().UtcDateTime, new(fetched, StringComparer.OrdinalIgnoreCase));
            Volatile.Write(ref snapshot, next);
            var temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next), token);
                File.Move(temporary, path, true);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            { logger.LogWarning(exception, "Could not persist informational leverage cache"); }
        }
        finally { gate.Release(); }
    }

    // No network, locks or waiting on the proposal path.
    public string Describe(string symbol, decimal entry, decimal target)
    {
        var calculated = StrategyCalculator.CalculateLeverage(entry, target);
        var text = $"\nلورج محاسبه‌شده: {F(calculated)}×";
        var current = Volatile.Read(ref snapshot);
        if (!current.Rules.TryGetValue(symbol, out var rules))
            return text + "\nحداکثر لورج ارز: نامشخص (اطلاعات هنوز در دسترس نیست)\nلورج قابل استفاده: نامشخص";
        var effective = BybitLeverageRules.Normalize(calculated, rules);
        text += $"\nحداکثر لورج ارز: {F(rules.MaximumLeverage)}×\nلورج قابل استفاده با این سقف: {F(effective)}×";
        if (calculated > rules.MaximumLeverage) text += " — محدود به سقف ارز";
        if (clock.GetUtcNow().UtcDateTime - current.UpdatedAtUtc >= TimeSpan.FromHours(24))
            text += "\n⚠️ اطلاعات سقف بیش از ۲۴ ساعت قدمت دارد؛ به‌روزرسانی در انتظار است";
        return text;
    }

    private static string F(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await RefreshAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Informational leverage refresh failed; keeping previous snapshot"); }
            var age = clock.GetUtcNow().UtcDateTime - Volatile.Read(ref snapshot).UpdatedAtUtc;
            var delay = age < TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) - age : TimeSpan.FromMinutes(15);
            await Task.Delay(delay, token);
        }
    }
}

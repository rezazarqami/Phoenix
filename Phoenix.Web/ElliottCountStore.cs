using System.Text.Json;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

/// <summary>Persists each symbol's counts from monthly down to the requested interval.</summary>
public sealed class ElliottCountStore(BybitDemoClient bybit, ElliottWaveAnalyzer analyzer, ILogger<ElliottCountStore> logger)
{
    private static readonly string[] Degrees = ["M", "W", "D", "240", "60", "15", "5"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory = Environment.GetEnvironmentVariable("PHOENIX_ELLIOTT_COUNT_DIR")
        ?? Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json"))!, "elliott-counts");

    public async Task<ElliottScenario?> AnalyzeAsync(string symbol, string interval,
        IReadOnlyList<BybitKline> currentCandles, CancellationToken token)
    {
        var target = Array.IndexOf(Degrees, interval.ToUpperInvariant());
        if (target < 0) return analyzer.Analyze(currentCandles).Scenarios.FirstOrDefault();
        await _gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(_directory);
            var context = new List<ElliottWavePoint>();
            ElliottScenario? active = null;
            for (var degree = 0; degree <= target; degree++)
            {
                token.ThrowIfCancellationRequested();
                var tier = Degrees[degree];
                var file = Path.Combine(_directory, $"{symbol.ToUpperInvariant()}-{tier}.json");
                CountSnapshot? previous = null;
                try
                {
                    if (File.Exists(file)) previous = JsonSerializer.Deserialize<CountSnapshot>(await File.ReadAllTextAsync(file, token));
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    logger.LogWarning(ex, "Rebuilding Elliott count for {Symbol} {Interval}", symbol, tier);
                }
                if (previous?.RuleSet != ElliottWaveAnalyzer.RuleSetVersion) previous = null;
                IReadOnlyList<BybitKline> recent;
                try
                {
                    recent = degree == target ? currentCandles
                        : await bybit.GetKlinesAsync(symbol, tier, previous is null ? 1000 : 50, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (degree != target)
                {
                    logger.LogWarning(ex, "Using saved Elliott count for {Symbol} {Interval}", symbol, tier);
                    if (previous?.Analysis.Scenarios.FirstOrDefault() is { } saved)
                        context.AddRange(saved.ContextWaves.Concat(saved.Waves)
                            .Select(w => w with { Degree = -(target - degree), Timeframe = tier }));
                    continue;
                }
                if (recent.Count < 30) continue;
                var missingOverlap = previous is not null && !recent.Any(c =>
                    previous.Candles.Any(old => old.OpenTime == c.OpenTime));
                var revised = previous is not null && recent.Any(c =>
                    previous.Candles.Any(old => old.OpenTime == c.OpenTime && old != c &&
                        old.OpenTime != recent[^1].OpenTime));
                if ((revised || missingOverlap) && degree != target)
                    recent = await bybit.GetKlinesAsync(symbol, tier, 1000, token);
                IEnumerable<BybitKline> source = previous is null || revised || missingOverlap
                    ? recent : previous.Candles.Concat(recent);
                var merged = source
                    .GroupBy(c => c.OpenTime).Select(g => g.Last()).OrderBy(c => c.OpenTime).TakeLast(1000).ToArray();
                var changed = previous is null || revised || missingOverlap || merged.Length != previous.Candles.Length ||
                    merged[^1] != previous.Candles[^1];
                var analysis = changed ? analyzer.Analyze(merged) : previous!.Analysis;
                if (changed)
                {
                    var temporary = file + ".tmp";
                    await File.WriteAllTextAsync(temporary,
                        JsonSerializer.Serialize(new CountSnapshot(ElliottWaveAnalyzer.RuleSetVersion, merged, analysis), Json), token);
                    File.Move(temporary, file, true);
                }
                var selected = analysis.Scenarios.FirstOrDefault();
                if (selected is null) continue;
                if (degree == target) active = selected;
                else context.AddRange(selected.ContextWaves.Concat(selected.Waves)
                    .Select(w => w with { Degree = -(target - degree), Timeframe = tier }));
            }
            if (active is null) return null;
            // The analyzer already retains all non-overlapping valid structures
            // on the requested interval. Higher degrees provide market context.
            return active with
            {
                Waves = active.Waves.Select(w => w with { Timeframe = interval }).ToArray(),
                ContextWaves = context.Concat(active.ContextWaves.Select(w => w with { Timeframe = interval })).ToArray(),
                Subwaves = active.Subwaves.Select(w => w with { Timeframe = interval }).ToArray()
            };
        }
        finally { _gate.Release(); }
    }

    private sealed record CountSnapshot(string RuleSet, BybitKline[] Candles, ElliottAnalysis Analysis);
}

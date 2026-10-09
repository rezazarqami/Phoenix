using System.Text.Json;
using System.Collections.Concurrent;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

/// <summary>Persists each symbol's counts from monthly down to the requested interval.</summary>
public sealed class ElliottCountStore(BybitDemoClient bybit, ElliottWaveAnalyzer analyzer, ILogger<ElliottCountStore> logger)
{
    private static readonly string[] Degrees = ["M", "W", "D", "240", "60", "15", "5", "1"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (CountSnapshot Snapshot, DateTime At)> _snapshots = new();
    private readonly string _directory = Environment.GetEnvironmentVariable("PHOENIX_ELLIOTT_COUNT_DIR")
        ?? Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json"))!, "elliott-counts");

    public async Task<ElliottScenario?> AnalyzeAsync(string symbol, string interval,
        IReadOnlyList<BybitKline> currentCandles, CancellationToken token)
    {
        var target = Array.IndexOf(Degrees, interval.ToUpperInvariant());
        if (target < 0)
        {
            var local = await analyzer.AnalyzeAsync(ElliottWaveAnalyzer.ClosedCandles(currentCandles, interval, DateTimeOffset.UtcNow), token);
            return local.Scenarios.FirstOrDefault() ?? ElliottCoverage.Unavailable(local.Coverage);
        }
        var gate = _gates.GetOrAdd(symbol, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(_directory);
            var context = new List<ElliottWavePoint>();
            ElliottScenario? active = null;
            ElliottCoverageReport? coverage = null;
            var dataIssues = new List<string>();
            for (var degree = 0; degree <= target; degree++)
            {
                token.ThrowIfCancellationRequested();
                var tier = Degrees[degree];
                var file = Path.Combine(_directory, $"{symbol.ToUpperInvariant()}-{tier}.json");
                var cacheKey = $"{symbol.ToUpperInvariant()}-{tier}";
                var cached = _snapshots.GetValueOrDefault(cacheKey);
                CountSnapshot? previous = cached.Snapshot;
                try
                {
                    if (previous is null && File.Exists(file)) previous = JsonSerializer.Deserialize<CountSnapshot>(await File.ReadAllTextAsync(file, token), Json);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    logger.LogWarning(ex, "Rebuilding Elliott count for {Symbol} {Interval}", symbol, tier);
                }
                if (previous?.RuleSet != analyzer.CacheVersion) previous = null;
                IReadOnlyList<BybitKline> recent;
                try
                {
                    recent = degree == target ? currentCandles
                        : previous is not null && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(45)
                            ? previous.Candles
                        : await bybit.GetKlinesAsync(symbol, tier, previous is null ? 1000 : 50, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (degree != target)
                {
                    dataIssues.Add($"{tier}:DataUnavailable");
                    logger.LogWarning(ex, "Using saved Elliott count for {Symbol} {Interval}", symbol, tier);
                    if (previous?.Analysis.Scenarios.FirstOrDefault() is { } saved)
                        context.AddRange(saved.ContextWaves.Concat(saved.Waves)
                            .Select(w => w with { Degree = -(target - degree), Timeframe = tier, Origin = "Context" }));
                    continue;
                }
                recent = ElliottWaveAnalyzer.ClosedCandles(recent, tier, DateTimeOffset.UtcNow);
                if (recent.Count < 30) { dataIssues.Add($"{tier}:InsufficientCandles"); continue; }
                var missingOverlap = previous is not null && !recent.Any(c =>
                    previous.Candles.Any(old => old.OpenTime == c.OpenTime));
                var revised = previous is not null && recent.Any(c =>
                    previous.Candles.Any(old => old.OpenTime == c.OpenTime && old != c &&
                        old.OpenTime != recent[^1].OpenTime));
                if ((revised || missingOverlap) && degree != target)
                    recent = ElliottWaveAnalyzer.ClosedCandles(await bybit.GetKlinesAsync(symbol, tier, 1000, token), tier, DateTimeOffset.UtcNow);
                IEnumerable<BybitKline> source = previous is null || revised || missingOverlap
                    ? recent : previous.Candles.Concat(recent);
                var merged = source
                    .GroupBy(c => c.OpenTime).Select(g => g.Last()).OrderBy(c => c.OpenTime).TakeLast(1000).ToArray();
                var changed = previous is null || revised || missingOverlap || merged.Length != previous.Candles.Length ||
                    merged[^1] != previous.Candles[^1];
                var analysis = changed ? await analyzer.AnalyzeAsync(merged, token) : previous!.Analysis;
                if (changed)
                {
                    var temporary = file + ".tmp";
                    await File.WriteAllTextAsync(temporary,
                        JsonSerializer.Serialize(new CountSnapshot(analyzer.CacheVersion, merged, analysis), Json), token);
                    File.Move(temporary, file, true);
                }
                // Reuse does not extend freshness; target bars always come from the caller.
                if (degree == target || previous is null || DateTime.UtcNow - cached.At >= TimeSpan.FromSeconds(45))
                    CacheSnapshot(cacheKey, new CountSnapshot(analyzer.CacheVersion, merged, analysis));
                if (degree == target) coverage = analysis.Coverage;
                var selected = analysis.Scenarios.FirstOrDefault();
                if (selected is null) continue;
                if (degree == target) active = selected;
                else context.AddRange(selected.ContextWaves.Concat(selected.Waves)
                    .Select(w => w with { Degree = -(target - degree), Timeframe = tier, Origin = "Context" }));
            }
            if (active is null)
            {
                coverage ??= new(0, 0, 0, 0, [], currentCandles.Count < 2 ? []
                    : [new(currentCandles[0].OpenTime, currentCandles[^1].OpenTime, "InsufficientCandles")]);
                return ElliottCoverage.Unavailable(coverage with { DataIssues = dataIssues });
            }
            // The analyzer already retains all non-overlapping valid structures
            // on the requested interval. Higher degrees provide market context.
            return active with
            {
                Waves = active.Waves.Select(w => w with { Timeframe = interval }).ToArray(),
                ContextWaves = context.Concat(active.ContextWaves.Select(w => w with { Timeframe = interval })).ToArray(),
                Subwaves = active.Subwaves.Select(w => w with { Timeframe = interval }).ToArray(),
                Coverage = active.Coverage is null ? coverage : active.Coverage with { DataIssues = dataIssues }
            };
        }
        finally { gate.Release(); }
    }

    private void CacheSnapshot(string key, CountSnapshot snapshot)
    {
        _snapshots[key] = (snapshot, DateTime.UtcNow);
        // Retain recent working context, not every 1000-bar symbol/tier forever.
        foreach (var entry in _snapshots.OrderBy(x => x.Value.At).Take(Math.Max(0, _snapshots.Count - 64)))
            _snapshots.TryRemove(entry.Key, out _);
    }

    private sealed record CountSnapshot(string RuleSet, BybitKline[] Candles, ElliottAnalysis Analysis);
}

namespace Phoenix.Web;

/// <summary>A fully isolated, non-trading queue for approved observation signals.</summary>
public sealed class ShadowSignalRuntime
{
    public ServerOrderStore Store { get; }

    public ShadowSignalRuntime()
    {
        var mainPath = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json");
        var directory = Path.GetDirectoryName(mainPath)!;
        Store = new ServerOrderStore(Path.Combine(directory, "shadow-signals.json"),
            Path.Combine(directory, "shadow-history.db"));
    }

    public async Task<ServerSignal> AddAsync(SignalCandidate candidate, string timeframe, bool lineMode,
        byte[] image, TechnicalFeatureSnapshot features, SignalSimilarityResult prediction,
        string? requestedByUsername, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var signal = new ServerSignal
        {
            Id = id, Symbol = candidate.Symbol, Direction = candidate.Direction,
            Ceiling = candidate.Ceiling, Floor = candidate.Floor,
            EntryPrice = candidate.EntryPrice, TakeProfit = candidate.TakeProfit,
            StopLoss = candidate.StopLoss, LastPrice = candidate.LastPrice,
            ExpirePrice = candidate.Direction == "Long" ? candidate.Ceiling : candidate.Floor,
            Status = "Pending", OrderLinkId = $"observe-{id:N}"[..36], CreatedAtUtc = DateTime.UtcNow,
            RequestedByUsername = requestedByUsername, Timeframe = timeframe,
            ChartMode = lineMode ? "Line" : "Candles", TechnicalFeatures = features,
            TargetSimilarityPercent = prediction.TargetPercent,
            StopSimilarityPercent = prediction.StopPercent,
            SimilaritySampleCount = prediction.SampleCount
        };
        await Store.AddAsync(signal, token,
            new SignalEvidence(timeframe, signal.ChartMode, image, features));
        return signal;
    }
}

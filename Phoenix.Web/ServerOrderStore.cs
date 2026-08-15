using System.Text.Json;
using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Services;

namespace Phoenix.Web;

public sealed class ServerSignal
{
    public Guid Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal Ceiling { get; set; }
    public decimal Floor { get; set; }
    public decimal PositionSizeUsdt { get; set; }
    public decimal? Leverage { get; set; }
    public string? LeverageSource { get; set; }
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal StopLoss { get; set; }
    public decimal? StopLoss2 { get; set; }
    public decimal? RiskFreePrice { get; set; }
    public decimal? LastPrice { get; set; }
    public decimal ExpirePrice { get; set; }
    public decimal ExpireActivationPrice { get; set; }
    public string ExpireStage { get; set; } = "Initial";
    public string Status { get; set; } = "Pending";
    public string OrderLinkId { get; set; } = string.Empty;
    public string? BybitOrderId { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? FilledAtUtc { get; set; }
    public decimal? AverageFillPrice { get; set; }
    public decimal? ExecutedQuantity { get; set; }
    public DateTime? TargetReachedAtUtc { get; set; }
    public DateTime? RiskFreeReachedAtUtc { get; set; }
    public DateTime? RiskFreeClosedAtUtc { get; set; }
    public string? StopLoss2OrderId { get; set; }
    public DateTime? StopLossReachedAtUtc { get; set; }
    public DateTime? ExpireAdjustedAtUtc { get; set; }
    public DateTime? ExpiredAtUtc { get; set; }
    public string? Outcome { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ExpireReason { get; set; }

    public static ServerSignal FromPreview(Signal signal, BybitOrderPreview preview, decimal? leverage = null)
    {
        var id = Guid.NewGuid();
        return new ServerSignal
        {
            Id = id,
            Symbol = preview.Symbol,
            Direction = signal.Direction.ToString(),
            Ceiling = signal.High,
            Floor = signal.Low,
            PositionSizeUsdt = signal.PositionSizeUsdt,
            Leverage = leverage,
            LeverageSource = leverage is > 0m ? "PhoenixFormula" : null,
            Quantity = preview.Quantity,
            EntryPrice = preview.Price,
            TakeProfit = preview.TakeProfit,
            StopLoss = preview.StopLoss,
            StopLoss2 = signal.TradePlan?.StopLoss2,
            RiskFreePrice = signal.TradePlan?.RiskFreePrice,
            ExpirePrice = signal.Direction == Phoenix.Core.Entities.Direction.Long ? signal.High : signal.Low,
            ExpireActivationPrice = preview.Price + 0.20m * (preview.TakeProfit - preview.Price),
            OrderLinkId = $"phoenix-{id:N}"[..36],
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public BybitOrderPreview ToPreview() => new(
        Symbol, Direction == "Long" ? "Buy" : "Sell", Quantity,
        EntryPrice, TakeProfit, StopLoss, Quantity * EntryPrice);

    public void ApplyPhoenixLeverage(BybitInstrumentRules rules)
    {
        Leverage = BybitLeverageRules.Normalize(
            StrategyCalculator.CalculateLeverage(EntryPrice, TakeProfit), rules);
        Quantity = BybitOrderPreviewBuilder.RoundToStep(
            PositionSizeUsdt * Leverage.Value / EntryPrice, rules.QuantityStep);
        LeverageSource = "PhoenixFormula";
    }
}

public sealed class ServerOrderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;
    private readonly SignalHistoryStore _history;
    private List<ServerSignal>? _signals;
    private bool _historyMigrated;

    public ServerOrderStore()
    {
        _filePath = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json");
        _history = new SignalHistoryStore(_filePath);
    }

    public async Task<IReadOnlyList<ServerSignal>> GetAllAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var signals = await LoadUnsafeAsync(token);
            if (signals.Aggregate(false, (changed, signal) => NormalizeTerminalState(signal) || changed))
                await SaveUnsafeAsync(signals, token);
            await MigrateHistoryUnsafeAsync(signals, token);
            return signals.Select(Clone).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(ServerSignal signal, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var signals = await LoadUnsafeAsync(token);
            signals.Add(Clone(signal));
            await SaveUnsafeAsync(signals, token);
            await _history.UpsertAsync(signal, "SignalCreated", token);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateAsync(ServerSignal signal, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var signals = await LoadUnsafeAsync(token);
            var index = signals.FindIndex(x => x.Id == signal.Id);
            if (index < 0) throw new InvalidOperationException("سفارش در صف پیدا نشد.");
            if (signal.Status == "Pending" && signals[index].Status != "Pending")
                return; // Never let a stale polling snapshot undo an atomic entry claim.
            signals[index] = Clone(signal);
            await SaveUnsafeAsync(signals, token);
            await _history.UpsertAsync(signal, "SignalUpdated", token);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var signals = await LoadUnsafeAsync(token);
            var signal = signals.SingleOrDefault(x => x.Id == id);
            var removed = signal is not null && signals.Remove(signal);
            if (removed)
            {
                await SaveUnsafeAsync(signals, token);
                await _history.MarkRemovedAsync(signal!, token);
            }
            return removed;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryClaimSubmissionAsync(Guid id, decimal price, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var signals = await LoadUnsafeAsync(token);
            var signal = signals.SingleOrDefault(x => x.Id == id);
            if (signal is null || signal.Status != "Pending") return false;
            signal.Status = "Submitting";
            signal.LastPrice = price;
            await SaveUnsafeAsync(signals, token);
            await _history.UpsertAsync(signal, "Status:Pending->Submitting", token);
            return true;
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<SignalHistoryItem>> GetHistoryAsync(int days = 30, int limit = 1000,
        CancellationToken token = default) => _history.GetAsync(days, limit, token);

    private async Task MigrateHistoryUnsafeAsync(List<ServerSignal> signals, CancellationToken token)
    {
        if (_historyMigrated) return;
        foreach (var signal in signals)
            await _history.UpsertAsync(signal, "ImportedFromQueue", token);
        _historyMigrated = true;
    }

    public static bool NormalizeTerminalState(ServerSignal signal)
    {
        if (signal.CompletedAtUtc is not null && !string.IsNullOrWhiteSpace(signal.Outcome)) return false;
        var candidates = new List<(DateTime At, string Outcome)>();
        if (signal.TargetReachedAtUtc is { } target) candidates.Add((target, "Target"));
        if (signal.RiskFreeClosedAtUtc is { } riskFree) candidates.Add((riskFree, "RiskFree"));
        if (signal.StopLossReachedAtUtc is { } stop) candidates.Add((stop, "StopLoss"));
        if (signal.ExpiredAtUtc is { } expired) candidates.Add((expired, "Expired"));
        if (candidates.Count == 0) return false;
        var first = candidates.MinBy(x => x.At);
        signal.CompletedAtUtc = first.At;
        signal.Outcome = first.Outcome;
        if (first.Outcome == "Expired" && string.IsNullOrWhiteSpace(signal.ExpireReason))
            signal.ExpireReason = signal.ExpireStage == "Target" ? "TargetAfterActivation" : "InitialBoundary";
        signal.TargetReachedAtUtc = first.Outcome == "Target" ? first.At : null;
        signal.RiskFreeClosedAtUtc = first.Outcome == "RiskFree" ? first.At : null;
        signal.StopLossReachedAtUtc = first.Outcome == "StopLoss" ? first.At : null;
        signal.ExpiredAtUtc = first.Outcome == "Expired" ? first.At : null;
        signal.Status = first.Outcome == "Expired" ? "Expired" : "Completed";
        return true;
    }

    private async Task<List<ServerSignal>> LoadUnsafeAsync(CancellationToken token)
    {
        if (_signals is not null) return _signals;
        if (!File.Exists(_filePath)) return _signals = [];
        await using var stream = File.OpenRead(_filePath);
        return _signals = await JsonSerializer.DeserializeAsync<List<ServerSignal>>(stream, JsonOptions, token) ?? [];
    }

    private async Task SaveUnsafeAsync(List<ServerSignal> signals, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporary = _filePath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, signals, JsonOptions, token);
        File.Move(temporary, _filePath, true);
    }

    private static ServerSignal Clone(ServerSignal signal) => new()
    {
        Id = signal.Id, Symbol = signal.Symbol, Direction = signal.Direction,
        Ceiling = signal.Ceiling, Floor = signal.Floor, PositionSizeUsdt = signal.PositionSizeUsdt,
        Leverage = signal.Leverage,
        LeverageSource = signal.LeverageSource,
        Quantity = signal.Quantity, EntryPrice = signal.EntryPrice, TakeProfit = signal.TakeProfit,
        StopLoss = signal.StopLoss, LastPrice = signal.LastPrice, Status = signal.Status,
        ExpirePrice = signal.ExpirePrice, ExpireActivationPrice = signal.ExpireActivationPrice,
        ExpireStage = signal.ExpireStage,
        StopLoss2 = signal.StopLoss2, RiskFreePrice = signal.RiskFreePrice,
        StopLoss2OrderId = signal.StopLoss2OrderId,
        OrderLinkId = signal.OrderLinkId, BybitOrderId = signal.BybitOrderId, Error = signal.Error,
        CreatedAtUtc = signal.CreatedAtUtc, SubmittedAtUtc = signal.SubmittedAtUtc,
        FilledAtUtc = signal.FilledAtUtc, AverageFillPrice = signal.AverageFillPrice,
        ExecutedQuantity = signal.ExecutedQuantity,
        TargetReachedAtUtc = signal.TargetReachedAtUtc, RiskFreeReachedAtUtc = signal.RiskFreeReachedAtUtc,
        RiskFreeClosedAtUtc = signal.RiskFreeClosedAtUtc,
        StopLossReachedAtUtc = signal.StopLossReachedAtUtc,
        ExpireAdjustedAtUtc = signal.ExpireAdjustedAtUtc, ExpiredAtUtc = signal.ExpiredAtUtc,
        Outcome = signal.Outcome, CompletedAtUtc = signal.CompletedAtUtc, ExpireReason = signal.ExpireReason
    };
}

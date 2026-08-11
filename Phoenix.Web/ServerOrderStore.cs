using System.Text.Json;
using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class ServerSignal
{
    public Guid Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal Ceiling { get; set; }
    public decimal Floor { get; set; }
    public decimal PositionSizeUsdt { get; set; }
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal StopLoss { get; set; }
    public decimal? LastPrice { get; set; }
    public string Status { get; set; } = "Pending";
    public string OrderLinkId { get; set; } = string.Empty;
    public string? BybitOrderId { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }

    public static ServerSignal FromPreview(Signal signal, BybitOrderPreview preview)
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
            Quantity = preview.Quantity,
            EntryPrice = preview.Price,
            TakeProfit = preview.TakeProfit,
            StopLoss = preview.StopLoss,
            OrderLinkId = $"phoenix-{id:N}"[..36],
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public BybitOrderPreview ToPreview() => new(
        Symbol, Direction == "Long" ? "Buy" : "Sell", Quantity,
        EntryPrice, TakeProfit, StopLoss, Quantity * EntryPrice);
}

public sealed class ServerOrderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;
    private List<ServerSignal>? _signals;

    public ServerOrderStore()
    {
        _filePath = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json");
    }

    public async Task<IReadOnlyList<ServerSignal>> GetAllAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return (await LoadUnsafeAsync(token)).Select(Clone).ToList(); }
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
            signals[index] = Clone(signal);
            await SaveUnsafeAsync(signals, token);
        }
        finally { _gate.Release(); }
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
        Quantity = signal.Quantity, EntryPrice = signal.EntryPrice, TakeProfit = signal.TakeProfit,
        StopLoss = signal.StopLoss, LastPrice = signal.LastPrice, Status = signal.Status,
        OrderLinkId = signal.OrderLinkId, BybitOrderId = signal.BybitOrderId, Error = signal.Error,
        CreatedAtUtc = signal.CreatedAtUtc, SubmittedAtUtc = signal.SubmittedAtUtc
    };
}

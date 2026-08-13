using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class BybitInstrumentCatalog(BybitDemoClient bybit)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string> _symbols = Array.Empty<string>();
    private DateTime _expiresAtUtc = DateTime.MinValue;

    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken token)
    {
        if (_symbols.Count > 0 && DateTime.UtcNow < _expiresAtUtc)
            return _symbols;

        await _gate.WaitAsync(token);
        try
        {
            if (_symbols.Count > 0 && DateTime.UtcNow < _expiresAtUtc)
                return _symbols;

            _symbols = await bybit.GetTradableLinearSymbolsAsync(token);
            _expiresAtUtc = DateTime.UtcNow.AddMinutes(15);
            return _symbols;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ContainsAsync(string symbol, CancellationToken token) =>
        (await GetAsync(token)).Contains(symbol.Trim(), StringComparer.OrdinalIgnoreCase);
}

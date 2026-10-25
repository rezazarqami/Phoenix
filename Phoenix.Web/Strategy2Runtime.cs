using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed record Strategy2Options(
    string? ApiKey, string? ApiSecret, string QueuePath, string HistoryPath,
    bool Enabled, decimal BalanceUsageRatio)
{
    public static Strategy2Options FromEnvironment()
    {
        var root = Environment.GetEnvironmentVariable("PHOENIX_STRATEGY2_DATA_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "data-strategy2");
        var ratioText = Environment.GetEnvironmentVariable("PHOENIX_STRATEGY2_BALANCE_RATIO");
        var ratio = decimal.TryParse(ratioText, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.995m;
        return new(
            Environment.GetEnvironmentVariable("BYBIT_DEMO_API_KEY"),
            Environment.GetEnvironmentVariable("BYBIT_DEMO_API_SECRET"),
            Path.Combine(root, "server-signals.json"),
            Path.Combine(root, "phoenix-history.db"),
            string.Equals(Environment.GetEnvironmentVariable("PHOENIX_STRATEGY2_ENABLED"), "true",
                StringComparison.OrdinalIgnoreCase),
            Math.Clamp(ratio, 0.50m, 0.999m));
    }
}

public sealed class Strategy2Runtime
{
    public Strategy2Runtime(Strategy2Options options)
    {
        Options = options;
        Store = new ServerOrderStore(options.QueuePath, options.HistoryPath);
        Client = new BybitDemoClient(new BybitDemoOptions(options.ApiKey, options.ApiSecret));
    }

    public Strategy2Options Options { get; }
    public ServerOrderStore Store { get; }
    public BybitDemoClient Client { get; }
}

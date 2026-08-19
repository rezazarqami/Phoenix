namespace Phoenix.Engine.Exchanges.Bybit;

public sealed record BybitDemoOptions(string? ApiKey, string? ApiSecret, bool IsReal = false)
{
    public string BaseUrl => IsReal ? "https://api.bybit.com" : "https://api-demo.bybit.com";
    public string EnvironmentName => IsReal ? "Real" : "Demo";
    public const int ReceiveWindowMilliseconds = 5_000;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    public static BybitDemoOptions FromEnvironment()
    {
        var real = string.Equals(Environment.GetEnvironmentVariable("PHOENIX_TRADING_ENVIRONMENT"),
            "Real", StringComparison.OrdinalIgnoreCase);
        return real
            ? new(Environment.GetEnvironmentVariable("BYBIT_REAL_API_KEY"),
                Environment.GetEnvironmentVariable("BYBIT_REAL_API_SECRET"), true)
            : new(Environment.GetEnvironmentVariable("BYBIT_DEMO_API_KEY"),
                Environment.GetEnvironmentVariable("BYBIT_DEMO_API_SECRET"));
    }
}

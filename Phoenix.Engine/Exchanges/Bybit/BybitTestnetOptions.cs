namespace Phoenix.Engine.Exchanges.Bybit;

public sealed record BybitDemoOptions(string? ApiKey, string? ApiSecret)
{
    public const string BaseUrl = "https://api-demo.bybit.com";
    public const int ReceiveWindowMilliseconds = 5_000;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    public static BybitDemoOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("BYBIT_DEMO_API_KEY"),
        Environment.GetEnvironmentVariable("BYBIT_DEMO_API_SECRET"));
}

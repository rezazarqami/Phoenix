namespace Phoenix.Engine.Exchanges.Bybit;

public sealed record BybitTestnetOptions(string? ApiKey, string? ApiSecret)
{
    public const string BaseUrl = "https://api-testnet.bybit.com";
    public const int ReceiveWindowMilliseconds = 5_000;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    public static BybitTestnetOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("BYBIT_TESTNET_API_KEY"),
        Environment.GetEnvironmentVariable("BYBIT_TESTNET_API_SECRET"));
}

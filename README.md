# Phoenix

Phoenix is a .NET 10 trading engine and WPF paper-trading studio. The current version is intentionally restricted to simulation and never connects to a live exchange.

## Projects

- `Phoenix.Core`: trading entities and states
- `Phoenix.Engine`: strategy, entry, execution, monitoring, and exchange abstractions
- `Phoenix.Studio`: desktop paper-trading dashboard
- `Phoenix.Test`: dependency-free automated smoke and domain tests

## Run

```powershell
dotnet build Phoenix.slnx
dotnet run --project Phoenix.Test
dotnet run --project Phoenix.Studio
```

## Current safety boundary

`PaperExchange` only stores orders in memory. No API key, network call, or real order placement exists in this version. A live exchange adapter must not be enabled until strategy, risk limits, persistence, and sandbox integration tests are complete.

## Bybit Testnet

Phoenix Studio can read the latest linear-contract price from Bybit Testnet and verify a Testnet account. The integration is pinned to `https://api-testnet.bybit.com`; Mainnet is not configurable.

For authenticated account verification, create keys on Bybit Testnet and define them outside the repository:

```powershell
[Environment]::SetEnvironmentVariable("BYBIT_TESTNET_API_KEY", "your-testnet-key", "User")
[Environment]::SetEnvironmentVariable("BYBIT_TESTNET_API_SECRET", "your-testnet-secret", "User")
```

Restart Visual Studio after setting the variables. Never commit, paste into source code, or share either value. The current milestone does not submit orders to Bybit; it only verifies public connectivity and authenticated wallet access.

Bybit or its network provider may reject requests based on the originating region. Phoenix reports that response in the dashboard and does not attempt to bypass it.

## Next milestones

1. Persist signals, positions, orders, and audit events.
2. Add market-price streaming and deterministic position lifecycle handling.
3. Add configurable account-level risk limits and emergency stop.
4. Integrate an exchange testnet adapter with secrets stored outside source control.
5. Run replay and testnet validation before considering any live mode.

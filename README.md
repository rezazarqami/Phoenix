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

## Next milestones

1. Persist signals, positions, orders, and audit events.
2. Add market-price streaming and deterministic position lifecycle handling.
3. Add configurable account-level risk limits and emergency stop.
4. Integrate an exchange testnet adapter with secrets stored outside source control.
5. Run replay and testnet validation before considering any live mode.

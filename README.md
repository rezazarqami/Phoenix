# Phoenix

## Phoenix Web (mobile/server foundation)

`Phoenix.Web` is the first always-on server and mobile PWA foundation. It includes:

- a responsive Persian signal-entry panel;
- an automatic one-second Bybit Demo public connectivity/price probe;
- optional Demo account authentication through existing environment variables;
- a persistent server-side signal queue API;
- a panel access key through `PHOENIX_PANEL_KEY`;
- a one-second Demo order worker with explicit `PHOENIX_DEMO_TRADING_ENABLED=true` activation;
- stable Bybit `orderLinkId` values persisted before submission to reduce duplicate-order risk.

Run locally with `dotnet run --project Phoenix.Web`. Deploy the repository root with the included `Dockerfile`.

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

## Bybit Demo Trading

Phoenix Studio can read the latest linear-contract price and verify a Bybit Demo Trading account. The integration is pinned to `https://api-demo.bybit.com`; Mainnet is not configurable.

Create the API keys while the main Bybit account is switched to Demo Trading, then define them outside the repository:

```powershell
[Environment]::SetEnvironmentVariable("BYBIT_DEMO_API_KEY", "your-demo-key", "User")
[Environment]::SetEnvironmentVariable("BYBIT_DEMO_API_SECRET", "your-demo-secret", "User")
```

Restart Visual Studio after setting the variables. Never commit, paste into source code, or share either value. The current milestone does not submit orders to Bybit; it only verifies public connectivity and authenticated Demo wallet access.

Phoenix can submit a manually confirmed linear Limit order to Bybit Demo with full-position market TP/SL protection. It stores the returned order ID in memory so the same UI session can request cancellation. Mainnet is hard-disabled, Market entry orders are unavailable, and no order is submitted without a confirmation dialog.

## Telegram notifications

The web service can send Persian notifications when a signal is queued, reaches entry, is accepted by Bybit Demo, is cancelled, fails, or touches its target, risk-free/SL2 activation, or stop-loss level. Configure the bot outside source control:

```text
TELEGRAM_BOT_TOKEN=<token from BotFather>
TELEGRAM_CHAT_ID=<private chat, group, or channel id>
```

`TELEGRAM_CHAT_ID` is optional. If it is omitted, send `/start` to the bot and Phoenix discovers the most recent chat automatically.

Telegram commands and inline approval/rejection buttons are protected by a persistent per-user allowlist.
An administrator manages it from **دسترسی تلگرام** in the Phoenix panel. A user can send `/myid` to
the bot to learn their numeric Telegram user ID; `/myid` does not grant access. Once the first allowlist
entry exists, only enabled IDs in that list can run commands or press action buttons. The optional store
location is configured with `PHOENIX_TELEGRAM_ACCESS_FILE` and defaults to
`/var/lib/phoenix/telegram-access.json`.

The public-signal and Strategy 2 bots are outbound notification bots. Their destination channels or groups
must be private, and their readers are managed using Telegram's channel/group member controls.

Telegram failures are logged but never block or change order execution. Price-level messages say that a level was touched; they do not claim an exchange fill without exchange confirmation.

## Persistent order queue

The **Order Queue** tab stores multiple prepared entries under the current Windows user's local application-data directory. Pending entries survive application restarts. A manual check only refreshes prices; automatic Demo submission starts only after the user explicitly enables monitoring for the current session. The monitoring interval is selectable (1, 2, 5, or 10 seconds) and defaults to 1 second. Orders for the same symbol share one ticker request per cycle. While monitoring is enabled and Phoenix remains open, pending orders are submitted once their directional entry condition is reached. Monitoring is intentionally off after every application restart.

Bybit or its network provider may reject requests based on the originating region. Phoenix reports that response in the dashboard and does not attempt to bypass it.

## Next milestones

1. Persist signals, positions, orders, and audit events.
2. Add market-price streaming and deterministic position lifecycle handling.
3. Add configurable account-level risk limits and emergency stop.
4. Integrate Demo order submission with secrets stored outside source control.
5. Run replay and Demo validation before considering any live mode.

## Elliott Wave Lab

Phoenix Web includes a separately authenticated market-analysis workspace at
`/analysis`. The main login page links to its own `/analysis/login` screen.
Configure its credentials outside source control:

```text
PHOENIX_ANALYSIS_USERNAME=<analysis username>
PHOENIX_ANALYSIS_PASSWORD=<strong analysis password>
```

The first analysis ruleset reads public Bybit linear-market candles, detects
configurable swing pivots, validates five-wave impulses, ranks up to three
scenarios, and reports Fibonacci ratios and invalidation levels. It does not
place orders and does not use exchange API credentials.

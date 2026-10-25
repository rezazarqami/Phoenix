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

After manual signal confirmation, Phoenix waits for the entry price (Long: last trade at or below entry; Short: at or above entry), then submits a linear Market Buy/Sell order with full-position market TP/SL protection. Entry requests contain no limit price or GTC instruction. Both WebSocket entry detection and polling fallback, including Strategy 2, use this market-entry path. Exchange credentials and the environment-specific trading enable flag are required. The returned order ID is persisted for status synchronization; submission is not treated as a confirmed fill.

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

### Signal review Elliott count

The batch signal selector also supports one-minute analysis with a separate
15-minute Telegram chart. Its candidate and probability use one-minute candles;
the image (including entry-time review) uses 15-minute candles. Candidates
whose direction-adjusted entry-to-target move is below 0.5% are discarded
before professional analysis and Telegram delivery.

The Telegram review chart counts each symbol from monthly through weekly, daily,
four-hour, one-hour and the requested review interval. Valid prior structures
are retained alongside the active count; lower-degree labels use smaller type
without label boxes or connecting strokes. Elliott hard rules still determine
whether a structure may be labelled, so an interval with no valid count is left
unlabelled instead of displaying a fabricated wave sequence.

Chart labels use fixed degree colors and sizes: monthly green (largest), weekly
blue, daily red, and one-hour black. Four-hour counts remain in the analysis
but are hidden on shorter images. On 15-minute and 5-minute images the active
count is an optional, smaller purple fifth degree. On longer images sparse
child waves may use purple. The footer shows the color key; an absent monthly
count is not invented.

Counts and the last 1,000 candles per symbol and interval persist in
`elliott-counts` beside `PHOENIX_QUEUE_PATH`, or in `PHOENIX_ELLIOTT_COUNT_DIR`
when set. Subsequent requests fetch only recent higher-interval candles and
reuse the saved analysis when unchanged. New candles extend the stored data;
changed historical candles trigger a fresh analysis, and a rule-set change
invalidates the saved count. Keep this directory on persistent storage in
production.

Phoenix Web includes an analysis workspace inside the main Phoenix panel.
Sign in once at `/login` and choose «بخش تحلیل» to open Crypto Markets,
Signal Lab and Elliott Wave Lab. All analysis pages and APIs share the
Phoenix session and viewer/editor/admin permissions. Legacy analysis login
URLs redirect to the shared flow; the former analysis credential variables
are no longer used. Logging out from either workspace signs out of both.
Configure the main credentials outside source control:

```text
PHOENIX_AUTH_USERNAME=<username>
PHOENIX_AUTH_PASSWORD=<strong password>
```

Elliott ruleset 3.0-pdf derives every pivot from the Close line regardless
of whether the requested presentation is line or candlesticks. It scans the
full visible history, prefers counts that explain a material part of the
chart, and renders the parent degree separately from validated lower-degree
subwaves. Supported candidates include impulse, developing impulse, truncated
fifth, diagonal, zigzag, flat, irregular/expanded flat, contracting or
expanding triangle, W-X-Y and W-X-Y-X-Z. Unbreakable rules invalidate a count;
Fibonacci ratios and alternation only rank surviving alternatives. The leading
count, its coverage, lower-degree count and invalidation are included in the
Telegram review. It does not place orders and does not use exchange API
credentials. The executable rule map is documented in
`docs/elliott-rules-fa.md`.

## Review dataset and completion notifications

Every new Telegram batch proposal is saved before delivery in `review-archive.db`
beside `PHOENIX_QUEUE_PATH` (or the default queue directory). This independent
archive retains PNG images, proposal-time candles and candidate levels, timeframe,
chart mode, delivery state, and the first valid Approved/Rejected decision.
Unanswered proposals are not rejections. Approval labels are saved before trade
submission; a later submission failure does not change the preference label.
Trade-history expiry compaction does not delete this review dataset.

An analysis administrator can download a date-range ZIP from Crypto Markets.
Exports contain a manifest and Approved/Rejected/Unanswered image folders with
JSON metadata and candles. Limits are 31 days, 2,000 proposals and 128 MiB of
uncompressed source data per export; use shorter ranges if exceeded. Nothing is
uploaded to an AI service automatically and no learned filtering is enabled.
Evaluate future rules chronologically, group duplicate proposals, and test in
shadow mode before suppressing candidates. Approval is not proof of profitability.
No automatic archive deletion is configured; include this database in backups.

## Optional AI signal input

The main panel can use the OpenAI Responses API with Structured Outputs to extract
the five required fields from a Persian or English signal message. AI never
calculates, recommends, submits, or changes a trade. The normal Phoenix validator,
calculator, Bybit instrument checks, and explicit submit button remain authoritative.

Configure the server outside source control:

```text
OPENAI_API_KEY=<server-side API key>
PHOENIX_OPENAI_MODEL=gpt-5-mini
```

`PHOENIX_OPENAI_MODEL` is optional. Never put the API key in browser JavaScript,
source control, or a public environment variable.

Each queued signal shows independent resemblance to completed Target and StopLoss
entry snapshots, not two outcome probabilities. The comparison uses class-balanced
historical dispersion of the approved technical readings; constant readings do not
dilute changing evidence, and each timeframe/window retains its own identity.
Distances use a 0.05 scale floor, a Gaussian RMS kernel, and exponential neighbor
weighting. At least five usable examples per outcome, 70% observed varying fields,
and five nearby examples in either outcome are required. A distant opposing class
can therefore show low resemblance without suppressing the comparison. If history
has no varying measurements, the legacy descriptive metric remains available.
Percentages retain one decimal and messages show the gap; a gap below five points
is described as close, not as a statistical confidence interval.

Live and reconstructed entry snapshots share the version-2 extraction path using
only candles closed by entry time. Existing earlier snapshots are upgraded in bounded
background batches without changing trade outcomes; unknown entry times are skipped.
Placing an order is not treated as an entry timestamp. Missing legacy market context
is not a zero reading. Historical reviews exclude outcomes completed later and
rebuild their distance scales using past-only samples; duplicated signal IDs and
self-comparisons are excluded.

A separate regularized outcome model preserves timeframe/window identity and must
beat the historical-frequency baseline on a later chronological holdout before
publishing any outcome estimate. Resemblance is never substituted for this estimate.

Private Phoenix and Strategy 2 completion notifications query their own account's
USDT `walletBalance` (not available USD balance or equity). The bounded five-second
lookup cannot prevent a result notification on API failure. The amount is labelled
as a snapshot at notification time: current target/stop events are price-based,
so exchange settlement is not guaranteed to have completed. Wallet balances are
not added to public signal broadcasts or Risk Free activation messages.

## Evaluation data download

In Crypto Markets, open «داده‌های ارزیابی احتمال تارگت و استاپ» and download
the ZIP (administrator only). Defaults to the preceding year; dates filter signal
creation in the browser's local timezone, with an exclusive next-day upper bound.
The authenticated `/api/analysis/evaluation/export` endpoint exports separate
`main.jsonl`, `observation.jsonl`, and `strategy2.jsonl` populations plus a manifest.
Exports refuse more than 2,000 rows per population or 64 MiB of uncompressed row
data instead of silently truncating. Only one archive is built at a time.

The schema includes outcomes, timestamps, trade levels, entry features and latest
features; latest snapshots and scores are NOT frozen proposal-time observations.
It excludes account identifiers, balances, quantities, order IDs, free-text logs,
credentials and images. Missing historical evidence stays null. Population reads
are sequential, not an atomic cross-database snapshot. This export does not alter
trading, learn a new model, or publish private trade data to GitHub.

### Mobile evaluation output

ZIP download uses the server attachment URL directly instead of a temporary blob
URL that some embedded mobile browsers cannot save. The panel does not claim the
file was saved. «نمایش داده‌ها بدون دانلود» displays JSON in the panel with copy
and next-page controls. Authenticated administrators can also read the same
endpoint with `format=json&offset=0&limit=100`; `nextOffset` is null at the end.
Read every page: a partial page explicitly reports `completeForRequestedRange=false`.
The limit is 1–100 rows per page, with an 8 MiB page-size guard; dataset range
limits and privacy allowlist still apply. No public token or additional access
is created. The existing login is required to read data directly.

## Personal workspace

The root `/` now opens the owner's private workspace with finance entries (toman),
vehicle service records and journal entries. `/phoenix` opens the existing trading
panel using the same `phoenix_session`; existing analysis and trading API paths
are unchanged. The configured `PHOENIX_AUTH_USERNAME` / `PHOENIX_AUTH_PASSWORD`
account is the owner. Other Phoenix accounts land on `/phoenix` and receive 403
for every `/api/personal` operation. Personal records are never available to those
accounts, including editor accounts.

Records live in `personal.db` beside `PHOENIX_QUEUE_PATH`, or the explicit absolute
`PERSONAL_DATABASE_PATH`. Use the existing persistent data volume and include this
file in server backups; never commit personal data to this repository. JSON export
is available inside the overview. Vehicle service costs are included once in total expenses and also shown as a
separate subtotal. Month summaries use the Persian month; displayed dates use
the Persian calendar.

Personal API responses are not HTTP-cached. After verified owner access, the
service worker stores the generic personal app shell for offline use and clears
it on logout. Device records and pending operations use owner-scoped IndexedDB. Existing
installed Android/PWA clients open the new workspace at their unchanged root URL.
Deployment remains the existing `Deploy Phoenix` workflow on `master`, targeting
server `91.216.104.210` through `/usr/local/sbin/deploy-phoenix`; no domain or
separate application deployment is required.

### Frozen proposal evidence

New analyzed approvals retain `proposalEvidence`: observed time, candidate levels,
timeframe, technical features and the original review prediction. Main and
observation queues keep this snapshot across entry reviews and outcome updates.
Research JSON/ZIP exports expose it separately from entry/latest snapshots.
Legacy records keep it null; current or entry features are never relabeled as
original proposal evidence. Capture time describes when analysis was observed;
it does not claim all source candles were finalized. This is data provenance for
future proposal-time validation, not a new probability model or trading rule.


See [Personal life workspace](docs/personal-workspace.md) for the new capture,
goals, decisions, craft, time, experiments, reports, offline sync and backup flows.
This development is isolated in `personal/life-workspace` for a later release.

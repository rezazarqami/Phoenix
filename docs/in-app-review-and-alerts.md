# In-app review and focused device alerts

Crypto Market now includes a review desk. The existing scan form starts a producer with no Telegram configuration requirement. Proposals and their frozen analysis/images are atomically saved beside `PHOENIX_QUEUE_PATH` in `in-app-proposals.json`. The desk supports register, observe, reject and rejection reasons using the existing archive and submission paths. Approval is an explicit trading action; previews and tests do not place exchange orders.

Capacity is ten pending proposals, including the selected one. There is no next-proposal analysis prefetch. At capacity the producer waits before further analysis; a decision releases a slot. Count mode also reserves pending items against the remaining target. Stop cancels scanning, retaining pending proposals. After server restart, pending proposals remain reviewable; scanning is restarted explicitly. Each API is authenticated and filters by owner; view-only sessions cannot POST. Claims are persisted before side effects. An interrupted/ambiguous submission stays blocked for manual investigation rather than being retried and creating a duplicate trade.

Review proposals are not sent to Telegram. In-app registration skips queued-signal Telegram sends. Existing lifecycle integrations remain, but review and submission do not require Telegram.

## Technical neighborhood v4

The v3 count required **every** independent feature group to have RMS distance <=1, making the effective neighborhood progressively smaller as context was added. V4 uses the already computed weighted technical RMS radius <=1 (`score >= exp(-0.5)`); support/resistance, uncapped level geometry, Ichimoku and planned trade geometry still veto distances >2. The fixed underlying units, uncapped distances, past-only archive cutoff, economic deduplication and direction/timeframe separation are unchanged. No nearest-N quota or forced positive counts is introduced. Real zero remains valid. Summary denominators, insufficient-feature counts, comparison version, example IDs and group distances make zeros auditable. This is resemblance, not a calibrated outcome probability. Tests cover moderate context mismatch, strong contradiction, sparse history, missing data, exact counts and genuine zeros. Production archive counts must still be checked after deployment; no production archive was modified by this change.

## Android 1.9.7

Allowed: near entry, confirmed exchange fill, protection steps, target, stop, risk-free exit, and expiry only when the signal previously approached entry. Registration, ordinary expiry, cancellation and manual-close messages are excluded. Server feed removes retired queued event kinds, and native delivery acknowledges retired messages silently so the cursor cannot stall.

Four bundled WAV tones have independent Android notification channels: crystal, pulse, beacon, soft. The in-app picker selects a channel and sends a preview; Android channel settings remain accessible. Signal and service small icons use a monochrome P. The foreground connection channel is silent/minimal, and routine connection changes update in-app state without reposting the notification. Android requires a foreground-service notification for this monitoring approach; the in-app connection-settings button opens the separate channel so users can hide it using system settings without disabling signal alerts. Existing channel importance is controlled by Android/user settings.

The APK build remains on the existing CI signing key and identity. This PR does not publish, deploy or merge automatically.


## Result journal and live positions

- Results are displayed newest first, with older pages appended below. Selected notification links still scroll to the selected event. New events insert above, and reading an older event preserves the scroll position.
- New main-account Target and RiskFree results store a USDT wallet balance snapshot after terminal persistence, independently of Telegram configuration. A failed balance read leaves the result intact. Archived results without a captured balance display “ثبت نشده”; they are never assigned the current balance. The journal updates a previously inserted card when its wallet snapshot becomes available.
- Signal Center → معاملات باز reads the main real account's `/v5/position/list` (all USDT linear positions, paginated), with the existing owner/admin session required. It displays exchange unrealisedPnl, mark/average prices, size, leverage, margin, liquidation, TP/SL and Phoenix protection stage. It refreshes every five seconds while the pane is visible, with no trading actions.
- Return percentage is exchange unrealisedPnl divided by exchange positionIM × 100; unknown or zero margin produces an unavailable percentage. This basis is labeled in the UI and may differ from Bybit views configured to use another price/margin basis. Reference: https://bybit-exchange.github.io/docs/v5/position
- A symbol/side position is an exchange aggregate: multiple matching live Phoenix signals are labeled aggregate rather than assigned a fabricated protection stage. On network errors the previous snapshot stays visible with its last-success time.
- Web-only update; no APK or Android signing changes. Prepare the pull request, then deploy by merging to master.


## Journal results tab and Telegram wallet recovery

The journal now uses the same existing Phoenix sky and logo assets as the home page, with scoped CSS. “همهٔ رویدادها” preserves the full lifecycle; “فقط نتایج” includes Target, RiskFree close and StopLoss only. The server filters before applying its 50-row pagination, and each view has an independent cursor/cache. All final-result cards end with the USDT wallet balance or an explicit unavailable label. New main-account stop-loss results now capture wallet snapshots too.

The owner can select a single-chat Telegram Desktop JSON export (maximum 2 MiB) in the journal's recovery section. The importer examines the latest 50 final outcomes, including removed archived signals, and only fills missing journal balances. It matches outcome, symbol, direction, all three price levels and a message time from 5 seconds before to 24 hours after the outcome. Conflicting balances or ambiguous trades remain unresolved. Existing snapshots are never overwritten; zero balances are valid. It stores message ID, message time and export SHA256 in a journal audit table, not the full message text. Import does not send Telegram messages, access today's wallet, modify trades or replay phone alerts.

Historical Telegram messages are not available from the current Bot API connection; an actual chat export is required to recover those values. Until supplied/imported, unknown historical balances remain unknown. Export recovery is owner-only and capped at the most recent 50 final results. Reference: https://core.telegram.org/bots/api#getting-updates .


## Automatic Bybit historical wallet recovery

After startup, and every 30 minutes, the primary owner receives a recovery pass over the latest 50 real final outcomes. The journal also offers a retry button and a read-only status display. Existing local/archived wallet snapshots are preferred; original archived outcome facts can seed missing journal rows. Existing balances are never overwritten.

For remaining entries the server fetches signed GET `/v5/position/closed-pnl` and `/v5/account/transaction-log` in UTC day windows padded by 15 minutes, fully paginating both endpoints. A close must uniquely match the signal symbol, closing side, full executed quantity, entry price (or the exact recorded risk-free close order ID), and outcome time within 15 minutes. It cannot match multiple Phoenix outcomes. All closing-order trade fills must be present, quantities must sum to closedSize and exactly one final fill must report remaining size zero. The server uses that fill's actual USDT cashBalance, without computing wallet values from PnL or querying today's balance. Ambiguous/incomplete/aggregated/old/permission-denied results remain unresolved.

Recovered balance, transaction/order references, balance timestamp and source are persisted in `device_wallet_recovery` alongside the journal payload. Bybit-sourced cards explicitly label the balance as after exchange settlement, which can differ from a later Telegram notification snapshot. Recovery neither places orders nor changes trading state nor replays alerts; network calls never hold trading/journal locks. Each pass has a 90-second timeout and a single-flight gate, and each page sequence has cycle/size guards. The journal is the durable recovered-history store; existing canonical trade fields remain unchanged.

Owner-only routes: GET `/api/mobile/history/wallet-recovery`, POST `/api/mobile/history/recover-wallets`. The configured main-account API key is used; Strategy 2 is excluded. Automatic execution on production begins only after deployment. API permission/data availability determine actual recovery count; the development tests do not prove production account access.

References: https://bybit-exchange.github.io/docs/v5/account/transaction-log and https://bybit-exchange.github.io/docs/v5/position/close-pnl .


## Active desk selection and live signal charts

Signal Center → Active Signals now uses compact keyed rows with a left checkbox, asset badge, direction, current status, per-row cancel and expandable chart/levels. Select All snapshots only unfilled Pending/Submitted records; newly arriving signals are not silently added, and records that fill during polling are removed from selection. The server limits selected cancellation to 100 distinct IDs and reuses the execution-gated SignalCancellationService, including exchange cancellation confirmation and partial-fill refusal. Selection never invokes close-position or trading-create endpoints.

The old direction-based bulk panel has been removed from Active Signals. The existing main-account Close All preview, confirmation, one-use close token and resume-entry controls now live in Open Positions; Strategy 2 remains excluded.

Authenticated GET `/api/signals/{id}/live-chart` finds the current or archived original signal and returns 150 current Bybit candles, a current ticker, the saved timeframe, and unchanged saved ceiling/floor/entry/TP/SL/protection levels. There is no reanalysis. The shared local Lightweight Charts widget refreshes every five seconds only for visible open charts, labels the last successful update, marks stale data explicitly on failure, preserves zoom, and aborts/disposes when the row closes or disappears. The journal now opens this live chart; the original frozen image remains under a separately labeled “تصویر زمان ثبت سیگنال” disclosure. Historical result text and price levels remain original even though the live chart displays today's market.

Tests cover selected IDs, fill races, unchanged unselected/open/submitting positions, submitted partial executions, owner/viewer routes, archived-level lookup, keyed row retention, chart disposal, 360/390/412px overflow, current price display and network-failure labeling. Publication does not require a new Android APK.


## Supplied animated home banner

The home hero uses the supplied `phoenix_original_seamless_quilted.html` image and its original 1172px tile period, 1672×941 drawing surface and 10-second horizontal loop. The exact embedded JPEG is retained (base64 SHA256 ef46cfe53e920bf9a460b58af0e8884dab30f8ada4fdad982e5ade7614f31038). The self-contained local HTML asset is decorative, sandboxed with scripts only, noninteractive and hidden from assistive navigation. Canvas cover sizing adapts to the existing hero; light header text and a contained fade preserve readability without overlaying navigation tabs or menus.

`hero-motion.js` pauses the animation offscreen or while the document is hidden. Reduced-motion users receive the same image as a still frame. Only the home hero changes; other screens keep their existing backgrounds. No new APK is required. Browser checks cover real moving frames, exact image identity, mobile/desktop coverage, menu layering, offscreen pause and reduced-motion behavior. Changes are prepared in a pull request, not deployed.

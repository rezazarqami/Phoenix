# Independent market risk radar

The six-card primary navigation includes Market Risk directly. Its calendar and live-monitor tabs share the existing Phoenix navy/cyan theme. This subsystem has no execution dependencies and exposes only an authenticated, uncached `GET /api/market-risk`. It never pauses entries, cancels orders, closes positions, changes strategy settings, or requests credentials. Alerts do not constitute approval for an action. Existing manual trading controls remain under the owner's control.

## Data and interpretation

The background observer polls public Bybit linear BTCUSDT/ETHUSDT candles and orderbooks approximately every 15–23 seconds, with an eight-second upstream deadline. Browser polling reads the cached snapshot and does not multiply exchange traffic. Four upstream requests run concurrently. Each source can fail independently; exchange identity, return code and timestamp are checked. Scores expire when fresh data is absent. There is no fallback to fabricated or zero-risk readings.

Eight indicators cover current one-minute candle movement, last completed one-minute USDT turnover, bid/ask spread, and near-price displayed depth for each asset. Depth covers at most 50 levels per side within 0.5% of the midpoint; it is not full-market liquidity. Liquidations, news classification and other exchanges are not included in this version.

Movement/volume baselines use up to 120 previous completed candles, excluding the scored volume candle. Orderbook baselines use six hours of stored observations, excluding the current timestamp, and need at least 60 observations. Median and median absolute deviation resist outliers. A scale floor avoids near-zero denominators. Rising movement/volume/spread and falling depth increase the score. Scores express deviation, not calibrated shock probabilities. Baselines adapt as new observations arrive; there is no claim of automatically trained predictive AI.

The overall score is the maximum of available individual scores. One score >=90 or at least two >=70 generates a red market alert. Scores >=60 show caution. A subsequent red market alert requires eight fully observed calm cycles below 45, plus at least five minutes since the previous alert. Missing data cannot rearm the alert or produce a normal overall state. Partial snapshots are labeled explicitly even when some known indicators are quiet.

Official BLS/BEA iCalendar feeds and the Fed calendar JSON provide timed events. Supported releases include CPI, payrolls, PPI, GDP, PCE, and relevant Fed events. These are selected US macroeconomic events, not an exhaustive crypto or global-news calendar. Sources refresh every six hours, retry failed retrievals after 15 minutes, and show last-success and failure status. Cached source data becomes stale after 12 hours. Unknown/all-day or ranged release times do not receive invented timestamps. New York time is converted with daylight-saving rules; UI displays Tehran time. BLS's US-Eastern alias is supported.

Calendar alerts are deduplicated for the 60-, 30-, and 5-minute windows ahead of a timed event. In-app alerts are distinct from trading events. A red badge and recent danger toast appear on the main page; the radar retains alert history. This version does not send OS/background-device or Telegram notifications. “Read” acknowledges only the browser-session display and has no trading effect.

## Persistence and operations

`PHOENIX_MARKET_RISK_PATH` can specify the independent JSON archive. Otherwise it lives beside `PHOENIX_QUEUE_PATH`, or under the application's data directory if that setting is absent. Atomic replacement stores at most seven days / 150,000 samples, 30 days / 1,000 alerts, cached calendar sources and deduplication markers. Writes occur approximately once per minute and on graceful shutdown. No trading data files are read or written by this component. Monitoring continues server-side while browser tabs are closed.

External feed coverage and live reachability must be assessed in production; tests use deterministic fixtures and do not demonstrate forecasting performance. No change to a trading rule or stop threshold should follow from these scores without separate evidence and owner approval.

## Verification

`MarketRiskTests` covers shock aggregation, inverse depth, insufficient data, partial/stale feeds, Eastern DST, real BLS timezone naming, ICS folding, Fed JSON parsing, read-only requests, persistence and absence of execution dependencies. `market-risk-ui-tests.cjs` checks tabs, keyboard navigation, red/unknown indicators, read acknowledgment, disconnection behavior and no trading requests at 320/390/1440 pixels. Shared-shell tests include direct primary navigation and viewer access; quote assertions follow the existing compact cards and current journal theme.

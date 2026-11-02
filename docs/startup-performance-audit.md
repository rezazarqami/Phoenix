# Whole-application startup audit

Scope: login, home, hidden manual/tracking sections, shared identity reads, live charts and open positions. This builds on the review-delivery fix; it does not change trading calculations, scheduling, entry rules, stop/target evaluation, risk alerts or similarity membership.

## Evidence and changes

| Work | Evidence it was unnecessary at startup | Result |
| --- | --- | --- |
| Four `/api/auth/me` reads | Active signals, app, bulk controls and positions read the same identity in the same document | One shared in-flight read, independent response bodies, five-second reuse, bounded timeout, failed reads are not cached; server still authorizes every protected action |
| `/api/instruments` and selected-symbol limits | Only the hidden manual form consumes these results | Load on first opening that form; preserve the catalog across tab changes, retry failed loads on reopening |
| `/api/signals` on home | The only consumer needed there is a count; the endpoint can migrate similarity and wait for exchange instrument rules | Home uses local `/api/signals/active-count`; visible tracking retains the original detailed endpoint and all its migration behavior |
| Chart library on every home load | `LightweightCharts` is used only when a live chart is expanded | Shared lazy load on first expansion, retryable failure; test expands, closes and reopens a real chart |
| BTC overview polling away from home | Only the home chart displays that result | Poll only on visible home; reuse a successful overview for its existing one-minute refresh period; explicit timeframe changes abort the superseded request |
| Embedded JPEG in hero HTML | Same image was base64-embedded in a non-cacheable HTML document on every navigation | Extract identical JPEG bytes to versioned private-cacheable asset; keep canvas, animation and artwork unchanged |
| Unversioned login artwork/font | Stable assets were revalidated at each visit | Version the existing logo, background and font references; HTML/auth responses stay uncached |
| Indefinite transport wait | Login/positions/chart code could keep disabled/busy state until an unbounded fetch resolved | Twelve-second read/login deadlines, restored retry controls; no trading POST gains automatic retry |

The original JPEG SHA-256 is `169016c62717041c8ac041ca81e3065ee91579bf6daf8f747719112200dcb549`, 446,101 bytes. Hero HTML changes from 597,036 bytes to 2,254 bytes. This is extraction, not a redraw or image recompression. Cold transfer eliminates base64 expansion; repeat navigation can reuse the image separately.

## Measurement

Using the same Chromium viewport (390 x 844), fixture API responses and static file server, a cold home load before this change made **13 API requests**, versus **7** after this change (46% fewer). Identity calls fell from four to one. Instrument catalog, instrument limits, full signal rows and timeframe-rule reads disappeared from initial home loading. Status, quotes, risk notices and entry-pause checks remained. This is a request-count comparison in a controlled fixture, not a claim about production latency or every ISP/VPN route.

Reproduce: run `Phoenix.Test/startup-browser-tests.cjs` with `MEASURE_STARTUP=1`. To compare a previous source snapshot, supply `PHOENIX_AUDIT_BASE` pointing to its wwwroot; unchanged files fall back to the current root. The test reports request counts without performing a trade.

## Preservation checks

- Shared identity consumers can each read their response; owner/viewer permissions remain enforced by the server.
- Hidden manual data is absent at startup; opening the form loads the original catalog and selected leverage once.
- Tracking still requests complete signals, and expanding/reopening a live chart loads the library exactly once and renders candles.
- Home still receives global risk warnings; the exchange status indicator retains its one-second cadence and existing freshness checks.
- Web and packaged native login scripts exclude duplicate submissions and recover from a hung login. The native asset change takes effect in a rebuilt APK; web/server startup changes apply to the hosted pages.
- Existing live position values, safe cancellation/confirmation flows, chart scale controls and review-delivery regressions are exercised independently of the outdated general menu test.

## Deliberately retained

Connection heartbeat, exchange checks, price monitoring, risk/calendar monitoring, proposal capacity/backpressure, historical learning, background order protection and legacy similarity/leverage migration remain. Their necessity cannot be disproved from page startup traffic, and they must not be removed as a performance shortcut. No proxy, TLS verification or password-hashing setting is weakened.

Residual DNS/TLS stalls, hosting restarts and resource exhaustion require contemporaneous production evidence. Reducing verified redundant work does not establish the cause of every reported outage.

# Creation, journal and management work audit

This change only affects browser data loading and rendering. It does not change order entry/exit logic, timeframe capacity, similarity, historical learning, protection or background monitoring.

| Finding | Evidence | Change |
| --- | --- | --- |
| Embedded automatic creation eagerly requested market data, learning counts and hidden proposals | Their consumers are hidden; browser test counts each endpoint before opening those sections | Load when the respective section is selected; standalone visible market counters retain their initial read |
| Manual editor and market list could both fetch the same market snapshot | Concurrent initialization and repeated hidden-attribute mutations | Shared in-flight request, 30-second navigation reuse, explicit refresh bypass and single initialization guard |
| Opening the observation tab triggered both a synthetic refresh click and a details toggle | Both handlers called the same endpoint | One activation event, single in-flight read, short reuse for duplicate activation; explicit refresh remains available |
| Batch status polling continued inside hidden frames | Document visibility alone does not account for CSS-hidden iframes | Pause ordinary status polling; resume on section/frame activation. Command uncertainty and stop verification continue independently |
| New journal rows rebuilt all existing cards and expanded live charts | Rendering used `replaceChildren` with newly created cards | Retain unchanged card nodes and live charts; stop removed/replaced charts immediately; preserve independent source/view states |
| Journal committed an ETag before parsing a complete body | A truncated response could advance the validator without the new events | Commit validators only after successful page validation and merging; do not persist unchanged state |
| Late source responses were discarded using the active source's state | Switching away while a response arrived required fetching the same missing data again | Populate only the request's original source cache, without replacing the active view. Invalidation after import and session expiry still discards stale requests |
| History eagerly loaded the chart library and hidden charts could poll | Static vendor script plus only local-document viewport checks | Lazy library loading and visible-frame/parent-viewport checks; deferred first mount after DOM attachment |
| Management repainted unchanged rows and filter buttons on every signal/rule read | Repeated equal JSON responses still rebuilt/moved nodes | Compare snapshots before repainting, preserve existing row order and focus; continue fetching fresh timeframe rules with a bounded deadline |

Read-only journal identity/history/recovery status, learning and observation reads now have bounded deadlines. Trading submissions, cancellation, user-requested wallet recovery/import and batch stop safeguards do not gain automatic retries.

Regression coverage includes retained chart identity after a new history event, immediate disposal, no unchanged persistence, failed-body ETag integrity, late source caching, no resurrection after 401, manual/market sharing, deferred approval loading, hidden-frame polling, stable management DOM and absence of trading POSTs in the performance fixture. Existing approval, batch command, journal pagination, owner isolation, manual chart geometry and cancellation tests remain.

The new browser fixture opens a coin through the current watchlist flow, enables the existing level toggle, and verifies that projected target P&L still responds to margin changes.

No production deployment is performed as part of this audit. Controlled request-count and DOM tests do not establish the cause of every ISP/VPN outage.

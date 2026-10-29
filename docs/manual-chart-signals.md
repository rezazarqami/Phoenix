# Manual chart signals

Crypto Markets has an eighth tab, **سیگنال دستی**, with a compact symbol rail using the same market-cap-ranked assets as the existing crypto search. It loads the local Lightweight Charts library only when needed. Entry, target, stop and expiry are chart handles supporting pointer/touch drag and keyboard arrows. The price axis remains logarithmic and supports vertical scaling. Trend lines, logarithmic Fibonacci and horizontal drawing tools use the same price coordinates.

Margin is USDT collateral. Quantity is floor(margin × leverage / entry / quantityStep) × quantityStep. Target/stop dollar estimates use that quantity and direction, exclude fees/slippage, and update during drag and on margin/leverage changes. Expiry has no PnL estimate.

Preview validates instrument membership, tick size, leverage step/cap and minimum quantity/notional without queueing a trade. Confirmation posts a stable request UUID; uncertain retries preserve the frozen payload and reuse that UUID. After a lost response, the user can leave confirmation and return to reconcile the same submission. A successful POST adds a Pending ServerSignal to the existing durable queue; no new execution worker is introduced. The explicit Manual leverage is excluded from formula backfills. Manual registrations are not duplicated into Strategy 2.

Protection and expiry use the current engine: expiry activation at 25% entry-to-target distance, first protection at 50% with 20% stop, and second protection at 75% with 50% limit plus 20% market stop. These derive from the selected entry/target and support Long and Short. Initial expiry is the selected expiry line; subsequent adjustment remains engine-owned.

Origin `ManualChart` is persisted in queue clones and archive payloads. Manual initial expiries retain their analysis levels instead of undergoing the automatic-signal compaction. The separate results report filters origin before pagination and aggregates lifetime manual counts independently from the 50 displayed rows. It includes active signals and terminal outcomes.

Validation:

- ManualSignalTests: explicit levels/leverage, Long/Short dollar estimates, existing protection thresholds, tick collisions, leverage cap rejection, queue/archive restart and manual-only expiry reporting.
- manual-signals-ui-tests.cjs: full Crypto Markets initialization at 390/1280 px, ranked symbol rail, touch/mouse drag, live PnL, keyboard tick movement, touch drawing, Short mode, preview before commit, same-payload retry after a network failure and separate results.
- Existing server and interface regression suites.

No production orders are placed by these tests.

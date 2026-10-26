# Technical neighborhoods and channel delivery

## Reproduced failures

The previous nearby counter used a weighted arithmetic average and `score >= 0.65`. Matching generic/neutral fields could compensate for important SR differences. The old extractor also clipped support/resistance distances to 10 ATR and cloud positions to [-1,1]; different distant levels therefore became identical. The available `TradeGeometry` was not compared. There was no explicit minimum positive count, but this combination could admit nearly an entire small reference cohort for many different queries.

Channel publication used one detached task at the 25% approach boundary, with no persisted retry of the initial send. If that task failed, public lifecycle delivery excluded the signal because it had no message ID. If the price jumped directly to entry, the approach publication branch could be bypassed. Completion during image generation could also prevent the message ID being saved because `UpdateAsync` rejects writes to completed signals. One failed market request aborted polling of later symbols in the same cycle. Multi-chat sending previously treated one successful destination as success for all.

These are code-level causes and deterministic reproductions, not a claim to have inspected the production Telegram delivery logs or production database.

## Neighborhood v3

Every eligible historical record is compared once, with duplicate/conflicting episodes and outcomes unavailable at the proposal timestamp excluded. The reference observation stage is selected before comparison; proposal evidence is preferred, and the existing explicitly labelled legacy entry fallback remains. Outcomes are used only after distances have been measured to split the counts into Target, StopLoss and RiskFree. Same versus other signal timeframes is a separate partition, not a multiplier that makes a technical match zero.

The score is `exp(-0.5 * weighted_mean(group_RMS_distance²))`. A record is nearby only when **each available group RMS distance is <= 1**. A good cloud match cannot compensate for a large SR or stop/target geometry mismatch. The radius is a versioned engineering neighborhood, not fitted to outcome frequencies. Its tolerances are explicit and auditable:

| Measurement | One normalized distance unit |
| --- | --- |
| Legacy support/resistance distances | factor 1.5 in (1 + distance/ATR) |
| Pivot density/strength | 0.25 in the saved bounded strength (one pivot before saturation) |
| Legacy cloud position/bias | 0.35 in the saved normalized coordinate |
| Raw cloud distance at entry/stop/target | 1 ATR |
| Raw nearest support/resistance | factor 1.5 in (1 + distance/ATR) |
| Raw nearby pivot counts | factor 2 in (1 + pivot count) |
| Multi-scale Fibonacci alignment | 0.25 in its saved ATR neighborhood alignment |
| Structure/regime/sweep/break/retest | 0.25 in its saved score |
| Planned target/stop ATR distances and ATR volatility | factor 2 |

Groups cover primary SR, Ichimoku, matched multi-scale levels, Fibonacci, multi-scale clouds, planned trade geometry, structure and volatility. Multi-scale matching preserves actual interval/window identity (80/240/720 candles). All shared measurements in a group contribute to RMS. A missing optional group does not become a matching zero. At least one measured SR distance, at least two shared SR measurements and positive ATR on both sides are required. Two legacy distances clipped at 10 ATR do not establish exact proximity.

Extraction v4 adds uncapped confirmed-pivot support/resistance distances, actual cloud-center distances and pivot counts at the **planned entry, stop and target**, for the primary and each available multi-scale window. Existing bounded fields remain compatible. Supports use confirmed two-neighbor lows, resistances confirmed highs; pivot count uses half an ATR around the level. This is a pivot geometry model, not a full historical bounce/role-reversal model. No missing old geometry is reconstructed from present-day candles.

There are no positive count floors, per-outcome quotas, or nearest-N limits on counting. The bounded top-12 `NearestExamples` list is an audit view only: signal ID, outcome, timeframe, score, membership and group distances. All comparable records contribute to counts, with rejected feature coverage counted separately. Legacy class-average score fields retain their pre-existing five-example display requirement; this does not gate the six nearby counters. RSI, volume, BTC alignment, price-action score and current distance to entry do not enter this metric.

Initial proposal evidence stays frozen at entry. Current chart rendering remains separate and never reruns similarity or changes the initial counts. Retired outcome probability stays disabled.

## Delivery outbox

The notification worker reconstructs pending work from persisted activation/entry and lifecycle snapshots, including recently removed terminal records in SQLite. It retries publication even when the signal has since completed, sends the initial message before its lifecycle events, and saves acknowledgments by signal, destination and event. A direct entry timestamp is a publication fallback when no approach sample was observed. Existing ledger `SinceUtc` and sent keys are respected on migration; a fresh ledger starts at process startup rather than replaying the entire archive.

Publication metadata has its own atomic store operation that accepts terminal signals and cannot change trading fields. Ordinary stale trading writes preserve that metadata. Signal numbers include archived records. Current charts are bounded by an 8-second fetch timeout, with the saved proposal image and then text as fallbacks; fallback images and delayed publications are labelled. An image rejection cannot permanently block a text alert or falsely acknowledge image delivery. Telegram `retry_after` is persisted per destination, so restarting does not bypass a rate limit. Missing reply parents use the documented `ReplyParameters.allow_sending_without_reply` option: https://core.telegram.org/bots/api#replyparameters .

Delivery is at least once. A Telegram success whose response is lost, or a crash between Telegram acceptance and local acknowledgment, can still cause a duplicate; Telegram does not supply an idempotency key for these sends. The ledger prevents replay of successfully acknowledged messages. No live Telegram messages were sent during tests.

## Validation

164 C# regression tests pass in Release, including:

- Exact 4/3/4 and 8/6/1 matching cohorts, then six genuine zeros for contradictory SR, cloud or planned geometry.
- Distinct technical queries selecting distinct neighbor sets, and missing evidence distinguished from measured zero.
- Uncapped extraction distinguishing previously identical clipped readings, existing history/future/duplicate protections, and frozen entry reviews.
- Initial send failure, completion during send, restart, removed records, stale writes, direct entry and two independently retrying destinations.
- Rejected photos falling back to text, persisted Telegram cooldown and a failed first market symbol not starving later symbols.

The continuous production archive has not been used to optimize these tolerances. Persisted example IDs/group distances make future calibration and investigation possible without changing historical snapshots or inventing measurements.

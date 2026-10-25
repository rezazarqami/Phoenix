# Observation outcome audit — 2026-10-15

## Finding before changes

`SignalBatchService` maps the Telegram `observe` callback to `ShadowSignalRuntime.AddAsync`
and links the review to the resulting signal. `SignalLearningService` already loads both
main and observation histories. Completed Target/StopLoss observations with entry timestamps
and entry fingerprints participate in descriptive similarity and the binary model's training
candidate pool. Inclusion is not evidence that the probability model passes validation.
RiskFree, expiry, and unresolved records are included in the separate fixed-window evidence,
not converted into binary losses. No extra inclusion switch was needed.

The shadow queue previously omitted risk-free levels, never tracked risk-free activation or
closure, and removed pre-entry expiries without an explicit outcome. One symbol request failure
also skipped all later symbols for that cycle.

## Prepared behavior

- New observations persist their protection levels and `ObservationPolicyVersion = 1`.
- Sampled price reaches the configured activation (default 75% of the path to target): record
  activation, without closing. A later return to protection (default 50%) records RiskFree.
- Target remains Target when reached first. A sampled gap beyond the original stop records
  StopLoss; no guaranteed protected fill or profit is inferred from an unobserved path.
- Entry samples already beyond the original stop resolve immediately instead of waiting for
  another sample. They remain excluded from strict binary training if entry and completion
  timestamps coincide, while their recorded event remains available to window analysis.
- Initial pre-entry expiry is explicitly archived as Expired, with its timestamp.
- Symbol and individual update failures are isolated. No exchange order or notification API
  is added to observation tracking.
- Legacy observations keep their original policy and labels. No invented historical risk-free
  events or retrospective migrations. Export exposes the policy version.
- Fixed-window evidence counts comparable legacy observations and suppresses mixed-policy
  percentage shares when present. Counts remain visible with a warning in Telegram and panel.
- Weighted effective sample size is explicitly not a count of independent market experiences.

## Data checked

The authenticated panel was read on October 15. Its live learning status showed 138 valid
entry patterns and one valid proposal pattern, with proposal probability still unvalidated.
All 26 research pages for creation dates 2025-10-15 through 2026-10-15 were read: 2,566 rows,
1,251 main, 64 observation, and 1,251 strategy2. Sequential live pages are not an atomic snapshot.

Observation outcomes: 8 Target, 12 StopLoss, 44 without a final outcome; 20 eligible entry
binary records. None of the entered observation records lacked entry features in this read.
Main: 90 Target, 111 StopLoss, 37 RiskFree, 772 Expired, 197 Cancelled, 3 ManualClosed,
41 without final outcome; 119 entry-binary eligible records and 11 entered records lacking
entry features. Strategy2 had zero eligible entry-feature binary records and is not silently
pooled into the main strategy model. Counts before economic deduplication differ from learning
status counts. Unresolved does not necessarily mean currently active; removed/stale rows exist.

## Interpretation for the discretionary workflow

The useful current output is evidence about previously approved setups with matching timeframe,
direction, trade geometry, trend, volume and structure, alongside all recorded exit categories
and data limitations. Approval predicts user acceptance; it is not a profitable-trade label.
Only-approve examples are intentionally selected as redundant exposures, so their outcomes
cannot be assumed independent of the executed signals from the same market episode.

Do not select a new indicator or activate a forecasting probability merely because its in-sample
win rate looks better. A future probability must be validated on frozen proposal-time evidence,
with chronological market-episode separation, consistent outcome policies, comparison against
a past-only baseline, and allowance for risk-free/unresolved outcomes. Current binary similarity
still describes the subset that ended Target/StopLoss; it is not an all-proposal success rate.

## Remaining limits

This is sampled-price paper monitoring, not exchange execution simulation or continuous tick
coverage. The two-second delay is after work; heavy entry analysis can extend actual intervals.
Entry-feature collection still has no retry after a failed capture. Advanced pre-entry expiry
differs from the real execution worker. Historical risk-free gaps cannot be repaired from the
stored last price alone. New policy observations must accumulate before validating their
outcome distribution. No new predictor, correlation weighting, or accuracy improvement is claimed.

## Validation and release

Local build passed (one existing nullable generic warning); 141 .NET checks passed, including
Long/Short activation and return, restart, terminal immutability, target continuation, stop gaps,
legacy behavior, expiry, and mixed-policy window suppression. Three existing Node UI suites and
JavaScript syntax check passed. Prepared locally; not pushed or deployed as part of this audit.

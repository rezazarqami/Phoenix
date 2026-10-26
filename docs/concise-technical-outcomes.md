# Concise technical evidence — 2026-10-16

The owner uses historical analogues to support discretionary level selection. The review is now two outcome-estimate lines and two historical count lines (same timeframe / other timeframes), each containing target, stop and risk-free counts. Diagnostic windows, recent subsets, weighted counts, subgroup breakdowns, payoff and similarity percentages are no longer displayed in Telegram, Phoenix cards or chart footers.

## Counts

- Read the complete main and observation archives, with no 30-day filter, 10-year cutoff or 5000-record limit. The UI history pagination is unchanged.
- Include completed RiskFree outcomes with entry-time technical snapshots, alongside Target and StopLoss. A risk-free activation followed by a target is a target outcome, not a second risk-free outcome.
- Count only completed observations with valid stored evidence. Do not fabricate features for old missing snapshots. Open, expired and cancelled records are not inferred to be losses.
- Preserve existing same-timeframe proximity thresholds and outcome-balanced technical distance. Count technically similar other-timeframe records separately without the timeframe-distance penalty; a daily analogue is no longer automatically rejected merely because the query is five-minute.
- Deduplicate IDs and economic episodes; exclude future outcomes from historical reviews. Completion duration is not a cutoff or feature.
- Retain proposal/entry stage separation for the probability model. Descriptive analogues still fall back to entry snapshots if proposal history is insufficient; those counts are historical analogues, not new-signal probabilities.

## Probabilities

The existing regularized, separately calibrated outcome model uses support/resistance distances, strengths at the proposed entry/stop/target and directional Ichimoku measurements, with multi-scale identity retained. Add exact stored ATR-normalized target/stop distances, reward/risk, distance to planned entry and target clearance relative to the nearest opposing level. These describe the conditional planned entry; they do not simulate invented future candles. Missing legacy geometry remains missing.

Never rename resemblance scores as probability or use neighbour frequencies as calibrated forecasts. Chronologically separated fit/calibration/audit gates remain required. These are conditional binary estimates given entry and an eventual target/stop exit, not three-outcome unconditional probabilities. Risk-free is counted separately and is never relabelled as a loss. The short conditional qualifier remains when a numeric prediction exists. Unvalidated estimates show “نامشخص”.

Replay on the locally retained October 14 export: 138 eligible main/observation binary records; the revised model is still unavailable under the validation gate. This is not a fresh production-data audit and does not establish a predictive improvement. No claim is made that extra indicators alone improve accuracy.

## Research basis

- https://www.newyorkfed.org/research/epr/00v06n2/0007osle.html — empirical support/resistance predictive power varies by market/source; historical FX evidence is not proof for these crypto signals.
- https://www.newyorkfed.org/research/staff_reports/sr125.html — distinct reversal/breakout mechanisms around levels motivate measuring actual level placement rather than counting indicators.
- https://scikit-learn.org/stable/modules/calibration.html — probability calibration and reliability must be checked out of sample. Brier score alone does not establish calibration.

Regression coverage includes six-year-old, three-day outcomes; separate timeframe groups; risk-free; deduplication; future exclusion; null probabilities; exact two-line support; missing versus observed geometry; and a database record older than the former ten-year history cutoff.

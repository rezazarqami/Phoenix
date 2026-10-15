# Probability and resemblance evidence

Resemblance percentages are independently normalized technical distances to target and stop examples. They are not probabilities and need not add to 100. The interface also reports comparable counts, nearby same-timeframe outcomes and the observation stage. A historical target frequency is descriptive and is only displayed with at least ten nearby same-timeframe examples; it is not a calibrated forecast or confidence interval.

Proposal-time and entry-time features now use closed candles. Version 3 marks this consistent extraction policy. Frozen version-3 proposal evidence is trained separately from entry evidence. Older proposal snapshots are not relabeled as closed-candle observations. Entry examples can still provide explicitly labeled descriptive references while proposal history grows, but the entry model cannot supply a proposal probability. Both models condition on entered signals eventually ending in Target or StopLoss; expiries, risk-free and manual closes are separate outcomes.

The outcome learner requires at least 100 unique completed examples and separates chronological training (60%), calibration (20%) and audit (20%). Labels unavailable at the next boundary are purged. Sigmoid calibration sees only the calibration block; the audit does not refit weights or calibration. Acceptance requires a 5% Brier improvement over historical frequency, better log loss and improvement in the recent half, with minimum class support and complete feature coverage. These are validation gates, not a guarantee of future performance. Predictions stay unavailable when a gate fails. A requested nonzero probability filter fails promptly if no proposal model is validated, rather than searching indefinitely or substituting resemblance.

Duplicate IDs and matching trade episodes (symbol, direction, timeframe, levels and observation minute) cannot inflate support. Conflicting outcomes within an episode are excluded. Historical predictions exclude results not yet known at the observation time.

## Reproducible exploratory audit

Run locally with Python, numpy and scikit-learn:

```sh
python research/probability_audit.py /private/evaluation.json /private/results.json
```

The six fixed candidates are the previous logistic model, ridge models with primary or compressed features, the previous neighbor method, family-balanced neighbors, and a timeframe prior shrunk toward overall frequency. Three expanding time blocks are evaluated with label purging. The audit repeats for the main-only cohort and a 24-hour label gap. Baselines always use exactly the same covered test examples. Day-cluster bootstrap intervals acknowledge some within-day dependence; they do not remove selection bias, cross-day dependence or multiple-comparison risk. Already inspected history is exploratory, not a new untouched validation set. No candidate is activated by this script. Do not commit input records or private results.

References:
- https://scikit-learn.org/stable/modules/calibration.html
- https://scikit-learn.org/stable/modules/generated/sklearn.model_selection.TimeSeriesSplit.html
- https://scikit-learn.org/stable/modules/neighbors.html
- https://proceedings.mlr.press/v70/guo17a.html

## Queue corruption recovery

JSON writes use unique temporary files, write-through and a disk flush before replacement, retaining the previous queue. On parse corruption, the damaged bytes are preserved and SQLite integrity and snapshot identity are checked. Removed records are excluded, unknown statuses and empty history refuse recovery. Active snapshots are restored as `RecoveryRequired`, retaining their original status and all order references without fabricating an outcome. New submissions in that store are blocked until exchange reconciliation; removing a quarantined record is also blocked. The Strategy 2 read-only check reports live exchange positions and matching order states without placing or cancelling orders. A missing order in the realtime endpoint does not prove it never existed. Quarantine cannot be safely cleared solely from an empty order lookup.

# Technical outcome evidence — prepared, not published

## Update: recorded outcomes in a fixed window

The report now evaluates all comparable histories in a fixed 24-bar window:
24 minutes (1m), 120 minutes (5m), 360 minutes (15m), 1440 minutes (1h),
5760 minutes (4h). Unsupported intervals abstain. The window is an evaluation
convention, not an optimal horizon and never an instruction to close a trade.
Frozen new proposal evidence records the evaluation policy version and horizon.

The earliest recorded target, stop, risk-free closure or expiry in the window
determines its category. Simultaneous timestamps are unknown. A final outcome
after the deadline cannot become an in-window success. Missing entry times,
stale observations, quarantined records and insufficient follow-up stay unknown.
No recorded resolution means exactly that, not proof that price hit no barrier.
Not-yet-mature windows are excluded from the mature denominator. The denominator
includes all mature categories, including unknowns: the displayed shares are
recorded frequencies, not calibrated target/stop probabilities and need not sum
to 100%. Other exits and risk-free results are not relabeled as stops.

Real and observation sources are shown separately. Membership uses a fixed,
outcome-independent technical distance plus same direction/timeframe and comparable
trade geometry. It deliberately does not use the binary outcome-trained distance
scale to select the all-outcome population. Economic duplicates collapse and
conflicting duplicate labels become unknown. History is limited to the existing
5000 records per source / 3650-day archive query; it is not a claim of full lifetime
coverage. Removed rows can contribute historical outcomes, but early removal
cannot establish continued follow-up.

The payoff display adds planned reward/risk and theoretical binary break-even
frequency `risk / (risk + reward)`. This assumes only target/stop exits and no
costs; it is neither net expected return nor the signal's success probability.
The original calibrated binary estimate is explicitly labelled conditional on
entry and a binary exit, without a fixed horizon. No new probability model is
enabled by this update. A future competing-risk model requires independent future
validation; unmonitored intervals cannot be filled with invented candle paths.

`research/outcome_windows.py INPUT.json OUTPUT.json` audits broad entry cohorts
without live-neighbor filtering. Export rows include window labels and deadlines
for reproducibility. All tests use local fixtures or mocked HTTP, without trading
or messaging side effects.

Additional primary sources:
- https://scikit-survival.readthedocs.io/en/v0.24.0/user_guide/competing-risks.html
- https://mlfinpy.readthedocs.io/en/stable/Labelling.html

This change adds descriptive technical cohorts, not an unvalidated probability.
The original nearby same-timeframe counts remain available. A stricter subset
also requires the same direction, positive correctly oriented trade levels,
stop-distance/entry ratios within a factor of two, and reward/risk ratios within
0.8–1.25. These symmetric fixed tolerances are engineering defaults, not learned
optimal thresholds. Missing or invalid geometry cannot match.

The report includes target/stop counts, distinct UTC observation days and symbols,
weighted effective sample size, the last 30 days relative to the query timestamp,
and the broader same-direction/same-timeframe baseline. Effective sample size
measures weight concentration; it does not establish independent observations.
Overlapping subgroups describe trend, relative volume and structure regimes.
Their counts must not be added together or interpreted as causal effects.
Proposal-to-entry fallback is explicitly labelled. Completed outcomes after the
query timestamp and duplicate economic events are excluded by the existing path.

New feature snapshots retain exact price-unit ATR, target/stop distance in ATR,
reward/risk and distance from observed close to planned entry in ATR. Old records
remain missing; their ATR is not reverse-engineered from a rounded percentage.
The temporary analysis signal now carries its actual entry/target/stop levels.

## Research protocol

`research/technical_groups.py INPUT.json OUTPUT.json` evaluates six prespecified
families: levels/cloud, structure, momentum, volume/volatility, higher-timeframe
and BTC context, and trade geometry. It fits fixed regularized logistic models
for all groups, each group alone, and each group omitted. Three chronological
expanding folds purge labels unresolved at fold start. Scaling and missing-value
handling use training data only. Comparisons use a smoothed same-timeframe
historical baseline. Repeat with main-only data and a 24-hour boundary gap;
uncertainty is bootstrapped by observation day. This is exploratory re-analysis
of previously inspected data, not independent validation or a feature-selection
license. No tested candidate is automatically enabled in production.

## Probability definition and next gate

The current dataset estimates outcomes conditional on entry and a recorded
target/stop resolution. A proposal can also never enter, expire, or exit risk-free.
Its unconditional target and stop probabilities therefore need not sum to 100%.
Before a future model is activated, freeze the observation stage, entry rules,
holding horizon, target/stop barriers and handling of simultaneous intrabar touches.
Record unresolved and other exits separately. Prefer explicit first-touch labels
with a time barrier; OHLC alone must not invent which barrier was reached first.
Collect point-in-time spread/liquidity data prospectively if added; today's order
book cannot reconstruct historical liquidity. Require calibrated, untouched
future-period evaluation and coverage reporting before claiming predictive skill.

## Delivery and verification

Expanded reports can exceed Telegram's photo caption limit. Both notifier paths
keep short captions unchanged; long reports follow the image as linked plain-text
messages. Action buttons appear only on the final report segment. No text is
truncated and partial failure is reported as failure. Verification uses an in-memory
HTTP handler; no Telegram message or trading order is sent by the tests.

Sources:
- https://scikit-learn.org/stable/modules/permutation_importance.html
- https://scikit-learn.org/stable/auto_examples/inspection/plot_permutation_importance_multicollinear.html
- https://hudsonthames.org/does-meta-labeling-add-to-signal-efficacy-triple-barrier-method/
- https://core.telegram.org/bots/api#sendphoto

Release constraint: local preparation only. Do not push, open a PR, merge, or
deploy without a subsequent explicit user instruction to publish this work.

# Wave evidence across declared timeframes

V10 supersedes the unpublished V9 candidate. It retains bounded ordered-path alignment, hard trade-plan gates, grouped auxiliary checks, original observation cutoffs, same direction/orientation and unknown-data abstention. The proposal footer remains only the three target/stop/risk-free counts.

The reference selector no longer excludes a normalized wave solely because its declared signal timeframe differs. The ordered path is sampled by price travel, and geometry is expressed relative to its own logarithmic anchor range; timeframe equality was therefore an extra fragmentation rule, separate from the requested wave/plan similarity definition. Timeframe equality is still the default for direct Compare and TechnicalSimilarity callers such as legacy outcome-window analyses. Cross-timeframe access is explicit only for proposal/reference membership (or an explicit matchTimeframe:false call).

Episode deduplication now recognizes the same physical price/time wave across timeframes. An 80% overlap of the larger timestamp span and anchor prices within 3% of the larger wave range count once per symbol/direction, regardless of interval labels. Separate market episodes remain separate. The representative is selected by feature distance, timestamp and ID, never by outcome.

Validation adds cross-timeframe inclusion of genuinely similar plans, continued exclusion of incompatible target/stop geometry, and cross-timeframe overlap deduplication. Counts may still be zero when no eligible wave satisfies the definition. There is no minimum count, result quota or success-directed parameter tuning. Private archive replay is not a complete-archive or out-of-sample performance claim.

A regression test also identified a reference-identity collision: different anchored waves sharing prices and an observation minute were grouped before wave comparison, and conflicting outcomes removed both. The episode identity now includes known wave anchors; later physical-overlap deduplication still collapses truly repeated waves. Missing-wave legacy identity behavior remains unchanged.

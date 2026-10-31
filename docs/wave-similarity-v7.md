# Wave similarity v7

The six existing same/other-timeframe Target, StopLoss and RiskFree counters now use the exact selected anchor wave, rather than SR neighborhoods around three trade levels. No probability or outcome-class average is returned. No additional historical examples/charts are built.

The versioned fingerprint uses log-price normalization: directional efficiency, same-direction bodies, body/wick ratio, adjacent candle overlap, maximum internal countermove, occupancy and wick rejection around entry, strongest body crossing of entry, and eight price-progress shape bins. It excludes Elliott labels, elapsed time, volume, RSI and other-market data. Existing logarithmic Fibonacci, directional 1% adjustment, trade levels and first-touch burn rules remain unchanged.

Membership is outcome-independent: every standardized feature distance must be at most one. Fixed tolerances in TechnicalResemblance are initial engineering thresholds, not fitted performance claims. Counts describe past matching waves, not a guaranteed success rate. No 90% constant exists.

Repeated symbol/timeframe/anchor/price pairs count once; contradictory outcome duplicates abstain. Only outcomes completed before the query enter comparison. Missing or incomplete anchors remain unknown, never neutral zeros. Reference fingerprints are cached by SignalLearningService. Legacy proposal waves are reconstructed from existing review-archive candles with original anchor timestamps in its background refresh; unavailable records do not match. Trade history and execution evidence are retained.

Live analysis uses the already-fetched primary candles and makes no additional context-market requests. The existing proposal layout is retained; v7 summaries show only the six counters, including literal zeros. Existing queued proposal numbers migrate without refetching charts; queues lacking a wave snapshot return zero rather than reuse the old SR score.

Tests cover all six buckets beyond old sample caps, independent-wave deduplication, future labels, dissimilar and missing fingerprints, price/time invariance, frozen wave extraction, JSON persistence, archive recovery and first-touch validity.

# In-app review and focused device alerts

Crypto Market now includes a review desk. The existing scan form starts a producer with no Telegram configuration requirement. Proposals and their frozen analysis/images are atomically saved beside `PHOENIX_QUEUE_PATH` in `in-app-proposals.json`. The desk supports register, observe, reject and rejection reasons using the existing archive and submission paths. Approval is an explicit trading action; previews and tests do not place exchange orders.

Capacity is ten pending proposals, including the selected one. There is no next-proposal analysis prefetch. At capacity the producer waits before further analysis; a decision releases a slot. Count mode also reserves pending items against the remaining target. Stop cancels scanning, retaining pending proposals. After server restart, pending proposals remain reviewable; scanning is restarted explicitly. Each API is authenticated and filters by owner; view-only sessions cannot POST. Claims are persisted before side effects. An interrupted/ambiguous submission stays blocked for manual investigation rather than being retried and creating a duplicate trade.

Review proposals are not sent to Telegram. In-app registration skips queued-signal Telegram sends. Existing lifecycle integrations remain, but review and submission do not require Telegram.

## Technical neighborhood v4

The v3 count required **every** independent feature group to have RMS distance <=1, making the effective neighborhood progressively smaller as context was added. V4 uses the already computed weighted technical RMS radius <=1 (`score >= exp(-0.5)`); support/resistance, uncapped level geometry, Ichimoku and planned trade geometry still veto distances >2. The fixed underlying units, uncapped distances, past-only archive cutoff, economic deduplication and direction/timeframe separation are unchanged. No nearest-N quota or forced positive counts is introduced. Real zero remains valid. Summary denominators, insufficient-feature counts, comparison version, example IDs and group distances make zeros auditable. This is resemblance, not a calibrated outcome probability. Tests cover moderate context mismatch, strong contradiction, sparse history, missing data, exact counts and genuine zeros. Production archive counts must still be checked after deployment; no production archive was modified by this change.

## Android 1.9.7

Allowed: near entry, confirmed exchange fill, protection steps, target, stop, risk-free exit, and expiry only when the signal previously approached entry. Registration, ordinary expiry, cancellation and manual-close messages are excluded. Server feed removes retired queued event kinds, and native delivery acknowledges retired messages silently so the cursor cannot stall.

Four bundled WAV tones have independent Android notification channels: crystal, pulse, beacon, soft. The in-app picker selects a channel and sends a preview; Android channel settings remain accessible. Signal and service small icons use a monochrome P. The foreground connection channel is silent/minimal, and routine connection changes update in-app state without reposting the notification. Android requires a foreground-service notification for this monitoring approach; the in-app connection-settings button opens the separate channel so users can hide it using system settings without disabling signal alerts. Existing channel importance is controlled by Android/user settings.

The APK build remains on the existing CI signing key and identity. This PR does not publish, deploy or merge automatically.

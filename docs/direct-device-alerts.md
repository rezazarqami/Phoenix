# Direct Phoenix device alerts (Android 1.9.6)

## Activation

Install the signed update over the existing standalone Phoenix app, log in to the primary account, then open **menu → اعلان‌های گوشی → فعال‌سازی اعلان زنده**. Grant the Android notification permission. The explicit activation starts a foreground monitoring service and its persistent connection notification. Use **تست اعلان گوشی** to verify Android display permissions and **تنظیم باتری گوشی** to set the app battery mode to unrestricted. The test notification is local; the separate connection status and last successful server contact verify server reachability.

The persistent notification has a stop action. The settings dialog can stop monitoring too. Logout stops it and does not copy the session cookie to JavaScript, logs or a separate credential file. On reboot/package update the receiver attempts to restore previously opted-in monitoring; if Android prevents a start, opening Phoenix resumes it. Force-stop and manufacturer power restrictions can interrupt delivery; the interface shows connection state rather than claiming unconditional background connectivity.

## Transport

This route talks only to `https://marketsignalist.ir/api/mobile/feed`. It does not need Telegram, VPN access to Telegram, Google Play Services, a Firebase project or a service-account credential. Existing FCM support remains optional. Telegram delivery remains enabled and is not a prerequisite for a device alert.

The endpoint requires the same authenticated primary/admin session as other mobile operations. It uses bounded 20-second long polling. The server persists monotonically ordered event IDs in a separate SQLite feed; a fresh activation bootstraps at the current cursor, avoiding a burst of historical messages. The native private preferences persist the cursor only after notifications are posted. Stable event IDs share a deduplication store with FCM. Successful HTTP responses do not imply user reading/acknowledgment. The worker reconnects with backoff and labels old recovered events as delayed. Existing events remain in the feed for seven days; capture reconstructs up to one day of recently persisted trading events, including removed terminal records. Session expiration requires signing in again.

Long-poll responses never rewrite WebView cookies, avoiding restoring a logged-out session from a response already in flight. Monitoring stops on authentication failure or account mismatch. The backend separates owner/public signals from another account's private/dedicated signals. Lock-screen public notification content remains generic; unlocked/private notification text includes symbol and outcome.

## Events

Created, NearEntry (also direct entry if the approach sample was skipped), Opened, FirstProtection, RiskFreeReached (second stage), Target, StopLoss, RiskFree, Expired, Cancelled and ManualClosed. The new first-stage timestamp is recorded when its actual order placement is acknowledged, not inferred retrospectively from the protection stage. RiskFree notifications include the actual first-limit/second-limit/safety-market exit label from the filled-order metadata.

Trading outcomes are persisted before awaiting protective-order cleanup or private Telegram/wallet network work. A blocked-wallet regression verifies that the direct feed can deliver a target while Telegram-side work is still blocked. Both the server feed and Telegram read persisted lifecycle facts independently. The direct feed does not wait for Telegram publication or Firebase delivery. Native monitoring uses Android's declared `specialUse` foreground service with a user-visible ongoing notification, explicit start/stop, a non-exported service and the documented subtype property. Platform reference: https://developer.android.com/develop/background-work/services/fgs/service-types#special-use .

## Validation

C# feed tests cover lifecycle ordering, metadata, private-route isolation, restart/removal recovery and enrollment without historic replay. HTTP tests cover anonymous 401, secondary-user 403, valid bootstrap and invalid cursor handling. Browser tests cover permission handoff, activation independent of Firebase, local test, stop, logout and account denial. Android tests consume actual JSON pages into NotificationManager, verify deduplication/cursor persistence, wrong-account/backward-cursor rejection and risk-free/delayed labels. CI verifies the same package/signing identity and in-place update preservation. No real exchange orders or Telegram messages are sent by these tests.

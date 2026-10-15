# Mobile security and notification foundation

## Release boundary

Prepared on `personal/life-workspace`. Production deployment remains restricted to `master`. The Android workflow builds a separately installed debug APK, runs native navigation tests, and creates a commit-specific GitHub prerelease for personal-branch pushes. The installable integrated client contains no sample-data preview. The online client still connects to `https://marketsignalist.ir`; GitHub publication does not update that server.

## Login and device lock

The purple/yellow/white login uses the existing Phoenix credentials and role checks. Owner goes to `/`; editors/viewers retain `/phoenix` and cannot use personal/mobile APIs. Passwords are cleared from the form after login and never written into JavaScript storage or APK configuration. The existing persistent HttpOnly/SameSite cookie is retained and renewed; this is not a promise that a revoked/reset session will never need login again.

Cookies carry Secure when the effective request is HTTPS. Forwarded headers are accepted only using ASP.NET's default trusted loopback proxies; configure KnownProxies/KnownNetworks explicitly if deployment uses another proxy. Do not trust arbitrary forwarded-proto headers. Login has a per-IP 10/minute fixed-window limit. Both unknown and incorrect credentials share the same response.

Android uses AndroidX BiometricPrompt with strong biometrics and device credentials on Android 11+, and a system credential activity fallback on older devices. A phone without a secure lock cannot open the client. The web content is covered and hidden synchronously on pause; no unlocked state or web snapshot is restored after process death. Screenshot/recents protection uses FLAG_SECURE. Cancellation leaves the cover in place. This is an app-local device lock, not server-side WebAuthn/2FA, and does not lock a separate browser session.

The Android bridge uses WebViewCompat.addWebMessageListener, one explicit production HTTPS origin, and a main-frame check. File access and mixed content are disabled; unknown links open outside the app only for http/https. Notification routes are allowlisted. No addJavascriptInterface is used. Packaged/foreign origins cannot invoke native capabilities.

## Local reminders

AlarmManager.setExactAndAllowWhileIdle schedules one-shot reminders. POST_NOTIFICATIONS, channel enablement and SCHEDULE_EXACT_ALARM access are checked before saving. PendingIntents have unique UUID-based data and FLAG_IMMUTABLE. Records are encrypted using an Android Keystore AES/GCM key; the key does not require UI auth because receivers need it for rescheduling. App sandbox + device lock still matter; do not call the whole WebView database encrypted.

Reboot, package replacement, clock/timezone changes and alarm permission restoration re-arm future reminders. Recently overdue reminders are processed once; older ones remain visible in history. The alarm receiver reads the stored UUID and ignores stale/cancelled alarms. Channel-off or permission revoked at delivery can suppress it. Timing is subject to Android/OEM restrictions; there is no medical alarm guarantee. Logout invalidates Push and cancels all local reminders, including logout from the Phoenix UI.

## Server Push and signal delivery

Local reminders and a local notification test do not require Firebase. Remote data-only messages use FCM; messages display generic text, not records/signal details, on the notification surface. Opening a notification always goes through the device lock.

Android build variables (public Firebase identifiers, no service-account secrets):

- `PHOENIX_FIREBASE_APP_ID`
- `PHOENIX_FIREBASE_API_KEY`
- `PHOENIX_FIREBASE_PROJECT_ID`
- `PHOENIX_FIREBASE_SENDER_ID`

Register the APK's actual package ID in Firebase (`ir.marketsignalist.phoenix.orbit` for this test client). The default Firebase app is initialized from these options. Messaging auto-init stays off until the owner opts in. New tokens are persisted locally and registered again with the authenticated server on resume. Disabling or logout invalidates the token even if server cleanup fails.

Server variable `PHOENIX_FIREBASE_SERVICE_ACCOUNT` points to an external private JSON service-account file with FCM permission. Do not commit it or upload it to the client. OAuth is signed locally with RSA, short-lived access tokens are reused in memory, and destination hosts are fixed Google endpoints. Device registrations live in a separate SQLite file beside personal data (`personal.db.mobile.db`); include it in private server backups. All endpoints require the owner cookie. An explicit test sends only a generic notification to registered owner devices. Unregistered FCM tokens are pruned on the explicit UNREGISTERED response; transient errors retain registrations. Signal delivery uses a durable per-device SQLite outbox. Event ids remain stable across retries and restarts. Acknowledged devices are not retried; disabled devices are removed from queued delivery. Transient failures back off up to five minutes. Events older than fifteen minutes are not delivered as current alerts. Android suppresses repeated event ids.

`MobileSignalWorker` observes persisted signals every five seconds, independently of Telegram or execution. It covers creation, entry, risk-free activation, target, stop, risk-free closure, expiry, cancellation and manual closure. Only the owner’s private signals and non-dedicated public signals are included. Signals before device enrollment/initialization are not replayed. New tokens schedule authenticated background registration through WorkManager; the private WebView cookie is never returned to JavaScript. Public Firebase identifiers may be supplied by the authenticated server status endpoint and persisted by the trusted native bridge, so they need not be embedded in every APK build. No signal generation/trading behavior or Telegram behavior changed. Scheduled personal reminders are local and are not uploaded to a server scheduler.

## Acceptance before production

Automated: shared login/role isolation, rate limiting, private device registration validation and idempotency, worker shell caching, native origin/deep-link restrictions, web/native bridge UI including denied capabilities and scheduling. Android build and complete .NET regression/access checks run in GitHub Actions.

On-device: first sign-in, subsequent launches, repeated Home/app-switch/lock/unlock, cancellation, invalid fingerprint to device PIN, app process death, no device lock, notification permission denial/revocation, channel suppression, exact-alarm denial, screen-off delivery, reboot restoration, cancellation, logout and FCM delivery with valid configuration. Native biometric and alarm execution cannot be verified by DOM tests.

Sources: Android biometric authentication, AlarmManager/exact alarm and notification permission documentation; AndroidX WebKit bridge documentation; Firebase Android setup and FCM HTTP v1 documentation.

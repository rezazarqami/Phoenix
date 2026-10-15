# Activate Orbit / Phoenix signal notifications

The code path is implemented, but live delivery must not be reported active until Firebase configuration and a real device delivery test pass.

1. Connect the official Firebase plugin to the Google account/project that will own this app. Register Android package `ir.marketsignalist.phoenix.orbit`. Enable the FCM HTTP v1 API.
2. Configure the server with the matching public Android app identifiers: `PHOENIX_FIREBASE_APP_ID`, `PHOENIX_FIREBASE_API_KEY`, `PHOENIX_FIREBASE_PROJECT_ID`, `PHOENIX_FIREBASE_SENDER_ID`. These are public app identifiers; they are provided only through the owner-authenticated status endpoint. The native bridge initializes and persists the Firebase app so reception also works in a background process.
3. Supply an external private service-account JSON file to the server process through `PHOENIX_FIREBASE_SERVICE_ACCOUNT`. Grant only the permissions needed to send FCM messages for that project. Restrict file access to the service account running Phoenix. Never put this file in git, an APK, a workflow log or a public artifact. The project must match the Android app.
4. Deploy the server, install APK 1.6 or later over 1.5, open profile/security settings, grant Android notification permission, enable the Phoenix notification channel, and enable server notifications. The phone registers its token using the existing owner session. No effect on another user's device or Telegram destination is implied.
5. Use the explicit server test. Verify receipt with the app foregrounded, backgrounded and the screen off; tapping the notification must require native unlock. A successful FCM API response alone is not proof the phone displayed it.
6. Observe the next real signal event. The signal observer does not create trades or change execution. It queues new/entered/risk-free/target/stop/expired/cancelled/closed events. The SQLite outbox retries failures per device with stable ids, excludes events before enrollment and expires alerts after fifteen minutes. Delivery is independent of Telegram. The app deduplicates retried event ids.
7. Disable notifications and verify no further notifications appear; unregister/logout cancels background registration and invalidates the local token. Token refresh schedules registration through WorkManager without opening/unlocking the UI.

Android force-stop and OS/user notification restrictions can prevent delivery; reopening the app may be necessary after force-stop. Local exact reminders are independent of Firebase. These are app reminders rather than guaranteed emergency alarms.

Automated tests use generated test credentials and a fake FCM transport; they do not send real notifications or trade on a live account. Firebase project access and real phone validation are distinct acceptance steps.

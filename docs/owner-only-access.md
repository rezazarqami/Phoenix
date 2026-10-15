# Exclusive primary-owner access

`OwnerOnlyAccess` defaults to true in appsettings.json. At startup Phoenix atomically clears its secondary-account store; the separately configured primary administrator and password remain unchanged. Login accepts only that administrator, all existing non-admin cookies are rejected on Phoenix, analysis and personal routes, and secondary-account creation returns 403. Restarting is idempotent.

A non-secret operational report is written alongside the user file as `users.json.owner-only.json` (or the overridden filename plus suffix). It contains only the configured primary username, zero remaining secondary accounts, startup removal count and UTC timestamp. Passwords, hashes and cookies are never reported. The credential file remains mode 0600. Trading/history/personal records and Telegram permissions are outside this account-removal operation.

The integration test uses disposable credentials, creates editor/viewer accounts before enabling the policy, and verifies physical removal, revocation of pre-existing cookies, preserved owner access, blocked creation and restart behavior. The legacy role-isolation integration suite explicitly disables this policy only in its disposable fixture.

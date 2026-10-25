# Orbit: calendar, accounting and tasks

Prepared on `personal/orbit-simple-calendar` from master `a252179`. This supersedes the earlier Orbit visual design. No push, merge, deployment or release is part of this change.

## Behavior

- Today opens on a Persian month grid (Saturday first). Each day shows a Persian date and its Gregorian day in English digits; the month heading shows both calendars. Today and the selected date have distinct states. Previous/next month, return to today, and dated task creation are available.
- The horizontal day strip initially shows today at its left edge and the following six days. Swipe left for future days, right for past days. The initial 91-day window grows by 28 days near either edge. Only tasks scheduled for a date appear in its count and detail list.
- Bottom navigation: Today, Accounting, My Path, My Tasks, Review. The former Experiences page and its creation shortcuts are removed. Existing decision/craft/experiment data remain in the database and backups.
- Accounting has manual income/expense entries, search/date filters, real totals and an inbox of unreviewed SMS expense drafts. Drafts and ignored SMS never count as spending. Confirming requires choosing a category; amount/date/title/notes remain editable.
- Finance and tasks have independent user-created categories and nested subcategories. No categories are prefilled. Categories use the existing private, versioned, offline-capable record store. Parents synchronize before descendants; server validation rejects missing parents, cross-scope links, duplicates and cycles.
- My Tasks groups tasks by the chosen category. A checkbox completes/reopens each task; completed items can be included explicitly. My Path is retained.
- Android no longer prompts for fingerprint, PIN or device authentication at startup or after backgrounding. Existing persistent username/password sessions remain in use. Screenshots are allowed. The Android version advances to 1.8 (code 9).

## Bank SMS

Android requests READ_SMS only after the user taps the accounting SMS button. Inbox candidates from the last 30 days are read after permission is granted, on app startup/resume and on opening Accounting; the browser cannot read SMS. No background receiver or default SMS app role is required.

The native reader limits the response to debit candidates with a currency and banking context, excluding OTPs and explicit credits. The local parser accepts only a single explicit, labelled amount with a rial/toman unit. It converts rial to toman and does not infer an amount from a balance or card/account number. Unknown/ambiguous formats are skipped. Bank-specific formats still need acceptance with representative messages on the actual phone.

Drafts preserve the SMS id/timestamp and original text in the owner's private store. Repeated scans skip a previously imported source, including confirmed and ignored entries. Deleting an imported transaction marks its source ignored so another scan does not recreate it. Reading happens when the app is opened or resumed, not while the app is closed. Each draft remains a proposal until the user reviews and categorizes it. No SMS is sent to Telegram or an AI provider.

Server and web assets must be released together so the new category kind and SMS source field are preserved. Android SMS/screenshot/biometric changes require installing the new APK; a web-only deployment cannot alter the installed Android wrapper. The existing personal-branch build workflow can publish an APK on push, so this preparation intentionally stays local until release authorization.

## Verification

- Release web build: success, zero warnings/errors.
- Complete .NET suite: 152 passed, zero failed, including new taxonomy persistence/cycle/scope and SMS draft/report tests.
- Personal HTTP integration: shared login, owner-only records, role isolation, revision conflicts, reports, backup/import and logout passed.
- JavaScript domain, Orbit calendar/SMS parsing, DOM/IndexedDB UI, service-worker and native bridge UI tests passed. Offline restart and concurrent edit/delete coverage retained.
- Real Chromium: all five pages at widths 320, 390, 800 and 1440, no horizontal page overflow or JavaScript errors. Today aligned with strip edge; scrolling to its future edge expanded 91 days to 119.
- Android debug APK 1.8 built successfully; all 19 Robolectric unit tests passed, including startup/background behavior and the cleared screenshot restriction.
- Physical Android permission prompts, bank-specific SMS formats and screenshot capture require device acceptance before final release.

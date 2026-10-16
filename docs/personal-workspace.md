# Personal life workspace (separate branch, not a production release)

Current navigation and calendar/accounting/task behavior supersede the initial flows below; see [Orbit simplification](orbit-simplification.md). The storage/access guarantees remain applicable.

Implemented choices: proposals **1–8 and 12**. No conversational AI over private
records (9), generic asset dossiers (10), or people/contact management (11) were
added. The original vehicle-service and finance screens remain available.

## User flows

| Area | Implemented behavior |
| --- | --- |
| Quick capture | Persian text/digit/explicit money parsing into an editable draft; optional browser speech recognition with explicit consent; microphone recording and receipt photos retained as private attachments. Nothing is committed just by parsing. Ambiguous shorthand and multiple amounts require manual review. |
| Today | Maximum three important tasks per day, due times, completed/waiting/cancelled states, overdue tasks, daily check-in, habit progress, timer and decisions due for review. |
| Daily check-in | Optional mood, energy, focus, sleep quality/hours and activity tags. One entry per day; missing observations are null, not zero. |
| Goals and projects | Success criteria, dates, state, archive and goal/project links on tasks, time, decisions and craft experiments. |
| Habits | Distinct completed days; daily target 1 or weekly target 1–7. Iranian week starts Saturday. Missing logging is not treated as an observed failure. |
| Decisions | Initial reason, alternatives, expected outcome and review date are preserved; actual outcome and review date are recorded separately. All edited records retain versions. |
| Craft knowledge | Materials, method, observation, conclusions and photo/audio attachments, with searchable notes and links to goals/projects. |
| Time | Persistent start/stop timer, one running session, manual minutes, links to a goal/project; stop derives minutes from timestamps. Sessions are limited to 24 hours and attributed to their entry date. |
| Life experiments | Non-overlapping baseline/trial dates and a chosen check-in metric. Shows observed averages, sample counts and calendar-day coverage. Fewer than five measurements in either group is labelled insufficient; results are descriptive, never causal claims. |
| Review | Current/previous week, Persian current month or custom dates; same-length previous period, category spending, time/check-in trends with gaps, linked work, overdue tasks and decision reviews, descriptive activity/focus comparisons, guided written reflection and print-to-PDF. |

No records or signals are sent to Telegram. No change to trading/order behavior.

## Storage and access

`PersonalStore` migrates the existing `personal_entries` database in place without
replacing old records. Adds independent revision/tombstone, history and attachment
metadata tables. Private files are under `personal-attachments` alongside
`PERSONAL_DATABASE_PATH` (or beside `PHOENIX_QUEUE_PATH`) outside `wwwroot`.
Only the existing configured owner/admin can reach personal endpoints. Viewer
and editor accounts retain Phoenix access and cannot read or modify personal
records, media, history, trash, reports or backups.

Writes use client UUIDs for idempotency. Updates/deletes use optimistic revisions;
conflicts remain on the device for explicit resolution. Linked parent records
cannot be deleted while referenced; archive them or remove references first.
Soft deletion is recoverable and does not remove media needed by history/trash.

Private media allow JPEG/PNG/WebP and supported audio formats, maximum 16 MiB
per file and eight attachments per entry. Stored names never determine paths.
Authenticated media routes use `nosniff`; no public static-media route is added.

## Offline behavior and privacy

After the first successful owner login, the service worker caches only the
application shell and its scripts/styles. API responses and private media are
never placed in HTTP caches. Records, pending operations and newly attached blobs
are kept in IndexedDB on that device. Offline saves survive restart. Sync sends
parents before dependent entries and media before their records; retries do not
duplicate records. Edits/deletes made while an operation is in flight remain in
the outbox. Validation/conflict failures are visible in Settings.

Logout (including the existing Phoenix logout endpoint) clears the shell cache
and device database. Switching to a different owner clears the previous device
snapshot; a non-owner login is denied. A network/edge outage must not erase pending
records. Speech recognition uses the browser's provider when supported and may
require Internet; it is optional, consent-gated and not an offline guarantee.
Audio recording and text entry are the fallbacks. Actual Persian recognition
accuracy needs validation on the user's supported phone/browser.

Offline data is stored in the browser profile; it is not an encrypted vault.
These explicit offline copies are part of the chosen offline workflow, and device
lock/profile access protects them. Do not claim end-to-end or at-rest encryption.

## Backup and restoration

Settings downloads an owner-only ZIP with active/archived synchronized entries
and their referenced media (maximum 128 MiB of uncompressed media). Trash and
historical revisions remain in the server database; include the full database and
attachment directory in server backups for complete historical recovery.
ZIP import validates records/references/media before writing, rejects conflicting
media and duplicate check-ins/habit days, and is additive: existing UUIDs and
revisions are not overwritten. Re-importing the same backup is safe. A device JSON
export also captures pending text records/outbox metadata; it explicitly excludes
binary attachments and is not a substitute for the full server ZIP.

## Verification and release

- Existing complete .NET regression suite plus workspace persistence, revision,
  provenance, constraints, missing-data reports and media backup/import tests.
- Owner/editor/viewer HTTP tests cover all personal route families and replay,
  conflicts, recovery, report and backup behavior.
- Pure JavaScript domain tests and DOM/IndexedDB UI tests cover every module,
  capture review, numeric fields, offline restart and concurrent edit/delete.
  DOM tests do not replace a real Android layout/microphone/speech acceptance run.
- Existing cancellation, batch and market UI checks are retained.

`Verify Phoenix` also runs on `personal/**` pushes. The production workflow remains
restricted to `master` and is guarded against branch workflow dispatch. This branch
is for later review/integration only. Do not merge or deploy it until the user
finishes the ongoing Phoenix work and requests the release.

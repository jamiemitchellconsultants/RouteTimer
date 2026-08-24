# RouteTimer: remaining implementation prompt

Continue building RouteTimer from the current `codex/route-timer` branch. Preserve existing commits and work only in the RouteTimer worktree. Use test-first development, run focused tests before implementation, then run `dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false` before each commit. Do not copy or commit the rider's personal FIT/GPX files.

## Current foundation

- .NET 10 solution with standalone Blazor WASM, API, services, domain, EF Core/Npgsql persistence, Docker Compose, Caddy fragment, and Keycloak realm template.
- FIT decoding, GPX parsing, route processing, basic cleaning, power lookup, and route prediction calculations exist, but several are simplified and do not yet meet the approved model specification.
- PostgreSQL migrations create uploads, profiles, predictions, and analysis jobs. Production Compose waits for PostgreSQL health and enables migration on startup.
- Rider profile and raw FIT uploads are persisted. Accepted FIT uploads enqueue persisted `ParseTraining` jobs.

## Highest-priority remaining work

1. Implement the hosted analysis worker. It must claim persisted jobs safely, decode the retained FIT upload, clean/validate it, persist activity summaries and samples, and record useful permanent-failure diagnostics without stopping the host.
2. Replace the current EF queue's simple query-and-update claim with PostgreSQL-safe atomic leasing (`FOR UPDATE SKIP LOCKED` or equivalent), lease renewal, success/permanent-failure/transient-failure states, bounded retries, and recovery of expired leases. Add real PostgreSQL integration coverage.
3. Add `TrainingActivity` and `ActivitySample` schema/repositories. Retain parsed samples and quality/exclusion summaries. Training eligibility must enforce the agreed minimum moving time and coverage thresholds.
4. Connect accepted upload processing to coalesced model rebuild jobs. Add immutable rider-model storage, power bands, calibration/coverage/validation metadata, and a current-model query.
5. Upgrade the simplified model implementation to the approved 8 gradient bands by 5 moving-duration bands, robust medians, evidence-duration/activity-count coverage, shrinkage, interpolation, and confidence reasons.
6. Implement whole-activity leave-one-out validation with median/p90 APE and the insufficient-data state for fewer than three eligible activities.
7. Make predictions durable: retain uploaded GPX, require persisted profile plus ready model, capture model/profile/assumption snapshots, enqueue processing, persist segments, and expose summary/detail/job endpoints.
8. Upgrade the route simulator to sequential physical integration with calibration, finite/non-negative safeguards, and conservative descent handling as described in `docs/superpowers/specs/2026-08-24-route-timer-design.md`.
9. Update API contracts and UI to show training activity quality, job progress, model readiness/validation, persisted prediction history, and detailed results. Add the requested text-only map/profile presentation only when the data workflow is complete; do not use diagrams in user responses.
10. Complete deployment hardening: add a web readiness health check, document migration/configuration/backup/rollback procedures, and validate an authenticated deployment only when deployment inputs are supplied.

## Known limitations to address

- `PostgresJobQueue` is now EF-backed but not yet concurrency-safe enough for multiple workers.
- The profile, upload, and job schema are incomplete relative to the approved specification.
- API training upload currently returns a synchronous response and does not expose job IDs/statuses.
- No background worker yet consumes `ParseTraining` jobs.
- The database migrations are applied at startup only when `Database__ApplyMigrations=true` (set in Compose).
- Domain and end-to-end test projects currently have no discovered tests.

Use the approved spec and plan as the authority:

- `docs/superpowers/specs/2026-08-24-route-timer-design.md`
- `docs/superpowers/plans/2026-08-24-route-timer.md`

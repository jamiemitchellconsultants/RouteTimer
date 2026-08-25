# RouteTimer: remaining implementation prompt

Continue building RouteTimer from the current `main` branch. Create a new `codex/step-9` branch in an isolated RouteTimer worktree before starting the next implementation step. Preserve existing commits and work only in that worktree. Use test-first development, run focused tests before implementation, then run `dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false` before each commit. Do not copy or commit the rider's personal FIT/GPX files.

## Current foundation

- .NET 10 solution with standalone Blazor WASM, API, services, domain, EF Core/Npgsql persistence, Docker Compose, Caddy fragment, and Keycloak realm template.
- The complete training-data, rider-model, validation, durable-prediction, and calibrated sequential-simulation workflow is implemented through Step 8. Remaining product work is concentrated in API/UI presentation and deployment hardening.
- PostgreSQL migrations create uploads, profiles, predictions, and analysis jobs. Production Compose waits for PostgreSQL health and enables migration on startup.
- Rider profile and raw FIT uploads are persisted. Accepted FIT uploads enqueue persisted `ParseTraining` jobs.

## Highest-priority remaining work

Steps 1 through 8 are complete. Continue with:

9. Update API contracts and UI to show training activity quality, job progress, model readiness/validation, persisted prediction history, and detailed results.
10. Complete deployment hardening: add a web readiness health check, document migration/configuration/backup/rollback procedures, and validate an authenticated deployment only when deployment inputs are supplied.

## Known limitations to address

- `TrainingActivity` does not yet retain the specified device/session metadata, start/end timestamps, distance, and ascent summaries. `AnalysisJob` does not yet retain progress, cancellation state, or complete lifecycle timestamps.
- API training upload persists files and enqueues background parsing, but currently returns `200 OK` with filename/outcome/error results and does not expose upload or job IDs; the approved contract requires `202 Accepted` with a result for every file.
- The database migrations are applied at startup only when `Database__ApplyMigrations=true` (set in Compose).
- The end-to-end test project currently has no discovered tests.

Use the approved spec and plan as the authority:

- `docs/superpowers/specs/2026-08-24-route-timer-design.md`
- `docs/superpowers/plans/2026-08-24-route-timer.md`

## Step 9 execution status (2026-08-25)

The approved Step 9 API/UI plan has been implemented in the isolated `codex/step-9` worktree through Task 14. The implementation includes durable presentation metadata and job lifecycle/progress, safe prediction deletion/publication races, final API contracts and endpoint modules, typed client/polling/shared components, dashboard/profile/training/prediction UI, and local route visualization/vendor assets.

Verification completed:

- API: 66 passed.
- Client: 64 passed.
- Domain: 15 passed.
- Persistence: 143 passed.
- Services: 270 passed.
- Route visualization npm tests: 5 passed.
- Client build: passed with zero warnings and zero errors.
- EndToEnd: no discovered tests.

The three bUnit failures previously recorded here are resolved. All three were defects in
the tests rather than in the components:

- The synchronized-selection test asserted on the first `selectMapSequence`/`selectProfileSequence`
  interop call, which is always the initial highlight pushed by `OnAfterRenderAsync`, never the
  propagated selection. It now asserts on the most recent call.
- The disposal test called `IRenderedComponent<T>.Dispose()`, which releases the test-side handle
  without unmounting the component, so `RouteMap`/`RouteProfiles` `DisposeAsync` never ran. It now
  awaits `BunitContext.DisposeComponentsAsync()`.
- The training upload workflow test advanced `FakeTimeProvider` twice without waiting in between,
  so the second advance fired before the rider-model rebuild poller had registered its timer and
  that timer could never elapse. Both training tests now wait for the poller to be parked on its
  timer before each advance.

`RouteMap` and `RouteProfiles` additionally now skip pushing an unchanged selection to JS, so a
render that does not change the selection no longer repeats the interop call.

Deployment/browser acceptance and the undiscovered EndToEnd suite remain outside this execution
environment.

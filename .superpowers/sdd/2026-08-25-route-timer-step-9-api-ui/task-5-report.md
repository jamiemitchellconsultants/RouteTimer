Task 5 complete.

Implemented:
- Added `ModelStatusResult` with the exact requested shape.
- Added `ModelStatusService` with prerequisite precedence for no-current-model states, current-model usability during rebuilds, failed rebuild status attachment, and `invalid-rider-model` translation for invalid persisted models.
- Added `ModelRebuildService.RequestAsync(CancellationToken)` with profile and eligible-activity prerequisite checks, stable request exception codes, and coalesced `BuildModel` enqueueing for `ModelSubject.Id`.
- Added `IJobRepository.GetLatestAsync(JobType, Guid, CancellationToken)` and Postgres-backed lookup.
- Injected `IJobProgressReporter` into `BuildModelJobHandler` and reported the six requested model build stages.
- Registered `ModelStatusService` and `ModelRebuildService` in API DI.

TDD / verification:
- RED: focused Services tests failed before implementation because `ModelStatusService` and `ModelRebuildService` did not exist.
- GREEN: focused Services command passed: 23/23.
- Full Services test project passed: 267/267.
- Postgres job tests passed: 27/27.
- API tests passed: 31/31.

Notes:
- Model-building algorithms and math were not changed.
- No subagents were dispatched.

## Task 5 fix round 1

Addressed deterministic latest model-build selection. `GetLatestAsync` now documents and enforces this ordering:

1. `CreatedAt` descending.
2. `UpdatedAt` descending.
3. `Id` descending as the deterministic final tie-break.

The regression seeds two matching `BuildModel` jobs with the exact same `CreatedAt`; the lower-Id failed job has the later `UpdatedAt`, so both repository selection and the status-service active/failed mapping consistently identify the failed rebuild.

TDD RED:

```text
$ dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --filter FullyQualifiedName~JobRepository_GetLatestAsync_prefers_updated_at_when_build_jobs_share_created_at --no-restore
Failed: 1, Passed: 0, Skipped: 0, Total: 1
Assert.Equal() Failure: Expected 00000000-0000-0000-0000-000000000001; Actual 00000000-0000-0000-0000-000000000002
```

TDD GREEN:

```text
$ dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --filter FullyQualifiedName~JobRepository_GetLatestAsync_prefers_updated_at_when_build_jobs_share_created_at --no-restore
Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 3 s
```

Focused model/status and Postgres job verification:

```text
$ dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter "FullyQualifiedName~RouteTimer.Services.Tests.Models" --no-restore
Passed! - Failed: 0, Passed: 95, Skipped: 0, Total: 95, Duration: 36 ms

$ dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --filter "FullyQualifiedName~RouteTimer.Persistence.Tests.Jobs.PostgresJobQueueTests" --no-restore
Passed! - Failed: 0, Passed: 28, Skipped: 0, Total: 28, Duration: 43 s
```

Full solution verification:

```text
$ dotnet test RouteTimer.slnx --no-restore
Passed! - Failed: 0, Passed: 267, Skipped: 0, Total: 267, Duration: 123 ms
Passed! - Failed: 0, Passed: 15, Skipped: 0, Total: 15, Duration: 29 ms
No test is available in RouteTimer.EndToEnd.Tests
Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 287 ms
Passed! - Failed: 0, Passed: 31, Skipped: 0, Total: 31, Duration: 3 s
Passed! - Failed: 0, Passed: 139, Skipped: 0, Total: 139, Duration: 44 s
```

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

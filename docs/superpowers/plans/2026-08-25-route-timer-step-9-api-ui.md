# RouteTimer Step 9 API and UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Complete, verify, and commit each task before starting the next task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the completed RouteTimer training, model, job, and prediction workflows through a stable authenticated API and a complete Blazor UI with locally bundled synchronized route visualization.

**Architecture:** Complete presentation data and job lifecycle persistence first, then add focused service queries/commands and resource endpoints. A typed client API boundary feeds small Razor pages and reusable status components; Leaflet and Chart.js are isolated behind disposable JS modules and consume only persisted prediction-detail segments.

**Tech Stack:** .NET 10, C# 14, ASP.NET Core minimal APIs, Blazor WebAssembly, EF Core 10.0.11, Npgsql 10.0.3, PostgreSQL/Testcontainers, xUnit 2.9.3, bUnit 2.9.0, Node.js built-in test runner, Leaflet 1.9.4, Chart.js 4.5.1.

**Spec:** `docs/superpowers/specs/2026-08-25-route-timer-step-9-api-ui-design.md`

## Global Constraints

- Begin execution from a clean `main` containing this plan and spec. Use `superpowers:using-git-worktrees` to create `.worktrees/step-9` on branch `codex/step-9`.
- Read the Step 9 spec and `docs/superpowers/specs/2026-08-24-route-timer-design.md` before changing code; the older design wins on overlap.
- Do not change route processing, power modelling, calibration, descent limits, validation mathematics, or sequential simulation.
- Preserve immutable rider-model versions and historical prediction snapshots when profile or training evidence changes.
- Require the authenticated `rider` role on every `/api` endpoint; only `/health/live` and `/health/ready` remain anonymous.
- Keep the file limit at exactly 50 MiB per FIT or GPX file and verify the streamed byte count rather than trusting `Content-Length`.
- Accept at most 10 FIT files in one training batch; configure the server request ceiling for 10 maximum-size files plus multipart overhead, while enforcing each file independently.
- Keep coverage ratios as `0..1` API values. Format percentages and units only in the client.
- Emit stable lower-case kebab-case error/reason/stage codes. Never expose worker IDs, stack traces, SQL, raw response bodies, FIT/GPX content, or internal exception messages.
- Use `TimeProvider` for new timestamps and polling delays; do not add new `DateTimeOffset.UtcNow` calls.
- Pin Leaflet to `1.9.4` and Chart.js to `4.5.1`; runtime client assets must contain no CDN dependency.
- Keep `wwwroot/vendor/`, `node_modules/`, and extracted `RouteTimerExamples/` untracked. Commit `package.json`, `package-lock.json`, build scripts, source JS, and source CSS.
- Use `Examples.zip` only for manual/full acceptance smoke checks. Extract with `unzip -q Examples.zip`; never commit extracted personal FIT/GPX files.
- Follow TDD: focused RED test, minimal GREEN implementation, focused verification, full solution verification, then commit. Never combine tasks into one commit or carry uncommitted changes into the next task.
- Run .NET tests with `-p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal` to prevent shared compiler/test-host interference.
- If an interface must change from this plan, stop and adjudicate the producer and every consumer before editing; record the ruling and cost in the execution ledger.

---

## File Structure

### Domain and services

- `src/RouteTimer.Domain/Activities/TrainingActivityMetadata.cs`: normalized immutable session/device summary.
- `src/RouteTimer.Domain/Jobs/AnalysisJob.cs`: durable lifecycle/progress aggregate.
- `src/RouteTimer.Services/Jobs/IJobProgressReporter.cs`: handler-facing progress boundary.
- `src/RouteTimer.Services/Training/TrainingActivityQueryService.cs`: list/detail use case.
- `src/RouteTimer.Services/Training/TrainingActivityDeletionService.cs`: deletion plus rebuild orchestration.
- `src/RouteTimer.Services/Models/ModelStatusService.cs`: readiness/current-model projection.
- `src/RouteTimer.Services/Models/ModelRebuildService.cs`: validated coalesced rebuild command.
- `src/RouteTimer.Services/Predictions/PredictionDeletionService.cs`: durable prediction cancellation/deletion command.

### Persistence

- `src/RouteTimer.Persistence/Entities/TrainingActivityEntity.cs`: nullable legacy-safe metadata columns.
- `src/RouteTimer.Persistence/Entities/AnalysisJobEntity.cs`: progress/lifecycle columns.
- `src/RouteTimer.Persistence/Repositories/TrainingUploadRepository.cs`: atomic FIT upload plus parse-job creation.
- Existing training, prediction, job, and model repositories own projections/deletion/transition checks.
- `src/RouteTimer.Persistence/Migrations/20260825090000_AddStep9PresentationData*.cs`: the single Step 9 schema migration.

### API and contracts

- `src/RouteTimer.Contracts/{Training,Models,Jobs,Predictions,Errors}`: presentation-neutral DTO records and stable codes.
- `src/RouteTimer.Api/Endpoints/{Profile,Training,Models,Predictions,Jobs}Endpoints.cs`: one resource group per file.
- `src/RouteTimer.Api/Errors/ApiProblems.cs`: RFC problem construction and service-exception mapping.
- `src/RouteTimer.Api/Uploads/MultipartUploadReader.cs`: bounded multipart boundary logic.

### Client

- `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`: complete testable client boundary.
- `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`: HTTP, JSON, multipart, and problem handling.
- `src/RouteTimer.Client/Jobs/JobPoller.cs`: deterministic two-second polling.
- `src/RouteTimer.Client/Components/*.razor`: reusable problem/job/model/confidence/visualization components.
- `src/RouteTimer.Client/Pages/{Home,Profile,Training,TrainingDetail,Predictions,PredictionDetail}.razor`: route-level state and orchestration only.
- `src/RouteTimer.Client/wwwroot/js/route-visualization.js`: disposable map/chart interop.
- `src/RouteTimer.Client/scripts/build-vendor.mjs`: deterministic local browser asset copy.

## Execution Preflight

- [ ] **Step 1: Create and verify the isolated worktree**

Use `superpowers:using-git-worktrees`, then run:

```bash
git branch --show-current
git status --short
test -f docs/superpowers/specs/2026-08-25-route-timer-step-9-api-ui-design.md
test -f docs/superpowers/plans/2026-08-25-route-timer-step-9-api-ui.md
```

Expected: branch `codex/step-9`, empty status, both documents reachable.

- [ ] **Step 2: Establish the baseline**

```bash
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
git diff --check
```

Expected: 410 discovered tests pass; EndToEnd reports no discovered tests; diff check is silent.

## Mandatory Per-Task Commit Gate

Every task must end in a task-scoped commit. Tasks 1–14 each produce the commit named in that task. Task 15 may produce separate review-fix commits when findings exist, followed by its required documentation commit. Do not begin Task N+1 until Task N is committed and the worktree is clean.

After every task's focused GREEN command and before its commit, run:

```bash
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
git diff --check
git status --short
```

Expected: all discovered tests pass, diff check is silent, and status contains only that task's declared files. Do not commit when unrelated files are present.

After the task's documented `git add` and `git commit` commands, run:

```bash
git status --short
git log -1 --oneline
```

Expected: status is empty and the latest commit is the task commit just created. Record its hash in the execution ledger, then and only then proceed to the next task. Never defer, squash together, or batch commits from multiple tasks during plan execution.

---

### Task 1: Parse and normalize training metadata

**Files:**
- Create: `src/RouteTimer.Domain/Activities/TrainingActivityMetadata.cs`
- Modify: `src/RouteTimer.Domain/Activities/CleanedActivity.cs`
- Modify: `src/RouteTimer.Services/Activities/ParsedFitActivity.cs`
- Modify: `src/RouteTimer.Services/Activities/ITrainingCleaner.cs`
- Modify: `src/RouteTimer.Services/Activities/FitActivityParser.cs`
- Modify: `src/RouteTimer.Services/Activities/TrainingCleaner.cs`
- Modify: `src/RouteTimer.Services/Training/ParseTrainingJobHandler.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/TrainingActivityRepository.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/RepositoryRoundTripTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Activities/ActivityFixtures.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/BuildModelJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/DescentLimitBuilderTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/ModelFixtures.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/ModelValidatorTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Physics/PhysicsCalibratorTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Activities/FitActivityParserTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Activities/TrainingCleanerTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Training/ParseTrainingJobHandlerTests.cs`

**Interfaces:**
- Produces: `TrainingActivityMetadata(string SourceFileName, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string? DeviceManufacturer, string? DeviceProduct, double? DistanceMetres, double? AscentMetres)`.
- Produces: `ITrainingCleaner.Clean(ParsedFitActivity activity, string sourceFileName) : CleanedActivity`.
- `CleanedActivity` adds required final property `TrainingActivityMetadata Metadata`.
- `ParsedFitActivity` adds `EndedAt`, `DeviceManufacturer`, `DeviceProduct`, and `DeviceAscentMetres` while retaining `DeviceDistanceMetres`.

- [ ] **Step 1: Extend FIT fixtures and write failing parser/cleaner tests**

Add explicit assertions equivalent to:

```csharp
[Fact]
public async Task Parse_returns_session_and_device_metadata()
{
    await using var fit = FitTestFileBuilder.CyclingActivity(
        startedAt: new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero),
        endedAt: new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero),
        totalDistanceMetres: 25_000,
        totalAscentMetres: 320);

    var parsed = await new FitActivityParser().ParseAsync(fit, CancellationToken.None);

    Assert.Equal(25_000, parsed.DeviceDistanceMetres);
    Assert.Equal(320, parsed.DeviceAscentMetres);
    Assert.True(parsed.EndedAt >= parsed.StartedAt);
    Assert.False(string.IsNullOrWhiteSpace(parsed.DeviceManufacturer));
}

[Fact]
public void Clean_carries_normalized_metadata_and_source_filename()
{
    var parsed = ActivityFixtures.WithPowerCoverage(1) with
    {
        DeviceManufacturer = "  Garmin  ",
        DeviceProduct = "  Edge  ",
        DeviceDistanceMetres = double.NaN,
        DeviceAscentMetres = -1,
    };

    var cleaned = CreateCleaner().Clean(parsed, "23940033376_ACTIVITY.fit");

    Assert.Equal("23940033376_ACTIVITY.fit", cleaned.Metadata.SourceFileName);
    Assert.Equal("Garmin", cleaned.Metadata.DeviceManufacturer);
    Assert.Equal("Edge", cleaned.Metadata.DeviceProduct);
    Assert.Null(cleaned.Metadata.DistanceMetres);
    Assert.Null(cleaned.Metadata.AscentMetres);
}
```

Also test missing session end falls back to the latest sample and an end before start throws `ActivityInputException("invalid-session-time", ...)`.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~FitActivityParserTests|FullyQualifiedName~TrainingCleanerTests|FullyQualifiedName~ParseTrainingJobHandlerTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: compile failures for missing metadata fields and the new cleaner signature.

- [ ] **Step 3: Add the exact immutable types**

```csharp
public sealed record TrainingActivityMetadata(
    string SourceFileName,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? DeviceManufacturer,
    string? DeviceProduct,
    double? DistanceMetres,
    double? AscentMetres);

public sealed record CleanedActivity(
    string Name,
    IReadOnlyList<CleanRideSample> Samples,
    TimeSpan MovingDuration,
    ActivityQuality Quality,
    TrainingActivityMetadata Metadata);

public sealed record ParsedFitActivity(
    string Name,
    ActivitySport Sport,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? DeviceManufacturer,
    string? DeviceProduct,
    IReadOnlyList<RawRideSample> Samples,
    TimeSpan? DeviceTimerTime,
    double? DeviceDistanceMetres,
    double? DeviceAscentMetres);
```

Update every constructor call explicitly. Do not add nullable/default metadata to avoid hiding missing mappings.

- [ ] **Step 4: Populate and normalize metadata**

In `FitActivityParser`, read `FileIdMesg.GetManufacturer()`, `GetProductNameAsString()` (fall back to `GetProduct()?.ToString()`), `SessionMesg.GetTimestamp()`, and `GetTotalAscent()`. Normalize with:

```csharp
private static string? NormalizeText(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value.Trim();

private static double? NonNegativeFinite(double? value) =>
    value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
```

Use the latest sample timestamp when the session timestamp is absent. Throw stable `invalid-session-time` when the resolved end precedes the start. Change the cleaner signature and construct metadata only after source-file normalization:

```csharp
var metadata = new TrainingActivityMetadata(
    Path.GetFileName(sourceFileName),
    activity.StartedAt,
    activity.EndedAt,
    NormalizeText(activity.DeviceManufacturer),
    NormalizeText(activity.DeviceProduct),
    NonNegativeFinite(activity.DeviceDistanceMetres),
    NonNegativeFinite(activity.DeviceAscentMetres));
```

Pass `upload.FileName` from `ParseTrainingJobHandler`. Preserve every existing cleaning/geometry result.

Update `TrainingActivityRepository.ToDomain` in this task so the new required constructor compiles before Task 2 adds columns. For the pre-migration entity shape, derive start/end from ordered samples and fall back to `CreatedAt`; use `entity.Name` as the temporary source filename and null optional metadata. Task 2 replaces this temporary mapping with persisted values.

- [ ] **Step 5: Run focused and full service tests**

Run the Step 2 command, then:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: PASS.

- [ ] **Step 6: Commit Task 1**

```bash
git add src/RouteTimer.Domain/Activities src/RouteTimer.Services/Activities src/RouteTimer.Services/Training src/RouteTimer.Persistence/Repositories/TrainingActivityRepository.cs tests
git commit -m "feat: retain training session metadata"
```

---

### Task 2: Add the single Step 9 presentation-data migration

**Files:**
- Modify: `src/RouteTimer.Domain/Jobs/AnalysisJob.cs`
- Modify: `src/RouteTimer.Persistence/Entities/TrainingActivityEntity.cs`
- Modify: `src/RouteTimer.Persistence/Entities/AnalysisJobEntity.cs`
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/TrainingActivityRepository.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/JobRepository.cs`
- Create: `src/RouteTimer.Persistence/Migrations/20260825090000_AddStep9PresentationData.cs`
- Create: `src/RouteTimer.Persistence/Migrations/20260825090000_AddStep9PresentationData.Designer.cs`
- Modify: `src/RouteTimer.Persistence/Migrations/RouteTimerDbContextModelSnapshot.cs`
- Test: `tests/RouteTimer.Persistence.Tests/PostgresMigrationTests.cs`
- Test: `tests/RouteTimer.Persistence.Tests/RepositoryRoundTripTests.cs`
- Modify: `src/RouteTimer.Persistence/Jobs/PostgresJobQueue.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/PredictionRepository.cs`
- Modify: `tests/RouteTimer.Api.Tests/Workers/AnalysisWorkerTests.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/Jobs/PostgresJobQueueTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/BuildModelJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionWorkflowTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Training/ParseTrainingJobHandlerTests.cs`

**Interfaces:**
- Produces the final `AnalysisJob` constructor consumed by Tasks 3–15.
- Adds nullable activity metadata columns and non-null job presentation columns.
- Replaces one active-job unique index with separate queued and running unique indexes.

- [ ] **Step 1: Write failing legacy migration and round-trip tests**

Add a PostgreSQL migration test that migrates to `20260824200000_AddSequentialSimulationModel`, inserts one legacy activity/job, migrates current, and asserts:

```csharp
Assert.Null(activity.DeviceManufacturer);
Assert.Null(activity.DeviceProduct);
Assert.Equal(createdAt, activity.StartedAt); // repository derives from first sample when columns are null
Assert.Equal(0, job.ProgressPercent);
Assert.Equal("queued", job.ProgressStage);
Assert.Equal(job.CreatedAt, job.UpdatedAt);
```

Add repository round-trip assertions for every fresh metadata field and `AnalysisJob` lifecycle value.

- [ ] **Step 2: Run persistence tests and verify RED**

```bash
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~PostgresMigrationTests|FullyQualifiedName~RepositoryRoundTripTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: compile/schema failures for missing fields.

- [ ] **Step 3: Add exact entity and domain fields**

`TrainingActivityEntity` adds:

```csharp
public string SourceFileName { get; set; } = string.Empty;
public DateTimeOffset? StartedAt { get; set; }
public DateTimeOffset? EndedAt { get; set; }
public string? DeviceManufacturer { get; set; }
public string? DeviceProduct { get; set; }
public double? DistanceMetres { get; set; }
public double? AscentMetres { get; set; }
```

`AnalysisJobEntity` adds:

```csharp
public int ProgressPercent { get; set; }
public string ProgressStage { get; set; } = "queued";
public DateTimeOffset? StartedAt { get; set; }
public DateTimeOffset UpdatedAt { get; set; }
public DateTimeOffset? CompletedAt { get; set; }
```

Replace `AnalysisJob` with this exact property order:

```csharp
public enum JobState { Queued, Running, Succeeded, Failed, Cancelled }

public sealed record AnalysisJob(
    Guid Id,
    JobType Type,
    Guid SubjectId,
    JobState State,
    int ProgressPercent,
    string ProgressStage,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? WorkerId,
    DateTimeOffset? LeaseExpiresAt,
    string? DiagnosticCode = null,
    string? DiagnosticMessage = null);
```

- [ ] **Step 4: Configure mappings and indexes**

Use max lengths: source filename `512`, device strings `128`, progress stage `64`. Map all lifecycle timestamps as `timestamp with time zone`. Add a check constraint named `CK_analysis_jobs_progress` for `ProgressPercent BETWEEN 0 AND 100`.

Replace `IX_analysis_jobs_active_type_subject` with:

```csharp
job.HasIndex(entity => new { entity.Type, entity.SubjectId })
    .IsUnique()
    .HasFilter("\"State\" = 'Queued'")
    .HasDatabaseName("IX_analysis_jobs_queued_type_subject");
job.HasIndex(entity => new { entity.Type, entity.SubjectId })
    .IsUnique()
    .HasFilter("\"State\" = 'Running'")
    .HasDatabaseName("IX_analysis_jobs_running_type_subject");
```

Map fresh activity metadata directly. For legacy rows with null times, repository mapping derives start/end from the first/last ordered sample and falls back to `CreatedAt` when there are no samples; device/distance/ascent remain null.

- [ ] **Step 5: Generate and normalize the migration**

```bash
dotnet ef migrations add AddStep9PresentationData --project src/RouteTimer.Persistence/RouteTimer.Persistence.csproj --startup-project src/RouteTimer.Api/RouteTimer.Api.csproj
```

Rename the generated pair and migration attribute to ID `20260825090000_AddStep9PresentationData`. Ensure `Up` performs these exact backfills before non-null constraints/indexes:

```sql
UPDATE training_activities AS activity
SET "SourceFileName" = upload."FileName"
FROM stored_uploads AS upload
WHERE upload."Id" = activity."UploadId";

UPDATE analysis_jobs
SET "UpdatedAt" = "CreatedAt",
    "StartedAt" = CASE WHEN "State" IN ('Running','Succeeded','Failed') THEN "CreatedAt" ELSE NULL END,
    "CompletedAt" = CASE WHEN "State" IN ('Succeeded','Failed') THEN "CreatedAt" ELSE NULL END,
    "ProgressPercent" = CASE WHEN "State" = 'Succeeded' THEN 100 ELSE 0 END,
    "ProgressStage" = CASE
        WHEN "State" = 'Running' THEN 'running'
        WHEN "State" = 'Succeeded' THEN 'completed'
        WHEN "State" = 'Failed' THEN 'failed'
        ELSE 'queued'
    END;
```

Then drop the old active index, make `SourceFileName`, `UpdatedAt`, and `ProgressStage` non-null, and create both partial unique indexes/check constraint. `Down` reverses only this migration.

- [ ] **Step 6: Run focused persistence and EF checks**

Run the Step 2 command, then:

```bash
dotnet ef migrations has-pending-model-changes --project src/RouteTimer.Persistence/RouteTimer.Persistence.csproj --startup-project src/RouteTimer.Api/RouteTimer.Api.csproj
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: tests PASS and EF reports no pending model changes.

- [ ] **Step 7: Commit Task 2**

```bash
git add src/RouteTimer.Domain/Jobs src/RouteTimer.Persistence tests
git commit -m "feat: persist step 9 presentation data"
```

---

### Task 3: Enforce job progress, lifecycle, cancellation, and rebuild successors

**Files:**
- Modify: `src/RouteTimer.Services/Jobs/IJobQueue.cs`
- Modify: `src/RouteTimer.Services/Persistence/IPredictionRepository.cs`
- Create: `src/RouteTimer.Services/Jobs/IJobProgressReporter.cs`
- Create: `src/RouteTimer.Services/Jobs/JobProgressReporter.cs`
- Modify: `src/RouteTimer.Persistence/Jobs/PostgresJobQueue.cs`
- Modify: `src/RouteTimer.Api/Workers/AnalysisWorker.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `tests/RouteTimer.Api.Tests/Workers/AnalysisWorkerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Training/ParseTrainingJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Training/TrainingUploadServiceTests.cs`
- Test: `tests/RouteTimer.Persistence.Tests/Jobs/PostgresJobQueueTests.cs`

**Interfaces:**
- Adds `ReportProgressAsync(Guid jobId, string workerId, int progressPercent, string stage, DateTimeOffset now, CancellationToken)`.
- Adds `CancelAsync(Guid jobId, DateTimeOffset now, CancellationToken)`.
- `CompleteAsync` and `FailAsync` gain a `DateTimeOffset now` argument.
- `JobProgressReporter.ReportAsync(AnalysisJob job, int progressPercent, string stage, CancellationToken)` supplies worker/time safely.

- [ ] **Step 1: Write failing queue invariant tests**

Cover all of these cases with real PostgreSQL:

```csharp
[Fact] public Task Progress_is_monotonic_and_owner_guarded();
[Fact] public Task Completion_sets_100_completed_and_all_terminal_timestamps();
[Fact] public Task Failure_preserves_progress_and_sets_safe_terminal_state();
[Fact] public Task Cancel_clears_ownership_and_makes_terminal_row_immutable();
[Fact] public Task Expired_lease_reclaim_preserves_started_time_and_progress();
[Fact] public Task Change_during_running_build_creates_one_queued_successor();
[Fact] public Task Concurrent_changes_coalesce_to_the_same_queued_successor();
```

Use `FakeTimeProvider` and literal timestamps; do not assert against wall-clock ranges.

- [ ] **Step 2: Run queue/worker tests and verify RED**

```bash
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~PostgresJobQueueTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~AnalysisWorkerTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: compile failures for new methods/fields and behavioral failures for successor coalescing.

- [ ] **Step 3: Finalize the queue interface and reporter**

```csharp
public interface IJobProgressReporter
{
    Task ReportAsync(AnalysisJob job, int progressPercent, string stage, CancellationToken cancellationToken);
}

public sealed class JobProgressReporter(IJobQueue jobs, TimeProvider timeProvider) : IJobProgressReporter
{
    public async Task ReportAsync(AnalysisJob job, int progressPercent, string stage, CancellationToken cancellationToken)
    {
        if (job.WorkerId is null) throw new InvalidOperationException("A claimed job is required.");
        if (!await jobs.ReportProgressAsync(job.Id, job.WorkerId, progressPercent, stage,
                timeProvider.GetUtcNow(), cancellationToken))
            throw new OperationCanceledException("The job is no longer owned by this worker.", cancellationToken);
    }
}
```

Validate percentage `1..99` and nonblank known stage before persistence. Repository updates require `Running` and matching worker; SQL must enforce `new >= existing`.

- [ ] **Step 4: Implement exact transition semantics**

Use the passed/injected time consistently. Enqueue sets `0/queued/CreatedAt/UpdatedAt`. Claim sets `Running`, stage `running`, `StartedAt ??= now`, `UpdatedAt = now`, increments attempts. Complete sets `Succeeded/100/completed`, terminal timestamps, clears ownership/diagnostics. Permanent failure sets `Failed/failed`; transient retry sets `Queued/queued`, preserves progress/start, clears terminal time. Cancel sets `Cancelled/cancelled` from queued or running only.

Add `Cancelled` to `PredictionState`. When cancelling a `PredictRoute` job without deleting its resource, atomically set the prediction to `Cancelled`, set its completion time, and store the stable warning `prediction-cancelled`. Task 6 may remove that cancelled prediction in the same deletion transaction.

For `BuildModel` coalescing, query queued first. If none exists, insert queued even when one running row exists; separate partial indexes allow exactly one of each. Retry a unique-race lookup once, matching the existing bounded race handling.

- [ ] **Step 5: Update worker and DI**

Pass `timeProvider.GetUtcNow()` to completion/failure. Treat `OperationCanceledException` from lost ownership as expected job cancellation when the host token itself was not cancelled; do not rewrite a cancelled row as failed. Register `IJobProgressReporter` scoped.

- [ ] **Step 6: Run focused and full persistence/API tests**

Run Step 2 commands, then:

```bash
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: PASS.

- [ ] **Step 7: Commit Task 3**

```bash
git add src/RouteTimer.Domain/Jobs src/RouteTimer.Services/Jobs src/RouteTimer.Services/Persistence src/RouteTimer.Persistence/Jobs src/RouteTimer.Api tests
git commit -m "feat: track durable job progress"
```

---

### Task 4: Add training upload, query, deletion, and handler progress services

**Files:**
- Create: `src/RouteTimer.Services/Persistence/ITrainingUploadRepository.cs`
- Create: `src/RouteTimer.Persistence/Repositories/TrainingUploadRepository.cs`
- Modify: `src/RouteTimer.Services/Persistence/ITrainingActivityRepository.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/TrainingActivityRepository.cs`
- Modify: `src/RouteTimer.Services/Training/TrainingUploadService.cs`
- Create: `src/RouteTimer.Services/Training/TrainingActivityQueryService.cs`
- Create: `src/RouteTimer.Services/Training/TrainingActivityDeletionService.cs`
- Modify: `src/RouteTimer.Services/Training/ParseTrainingJobHandler.cs`
- Modify: `src/RouteTimer.Api/Program.cs` registrations only.
- Test: `tests/RouteTimer.Persistence.Tests/TrainingUploadRepositoryTests.cs`
- Test: `tests/RouteTimer.Persistence.Tests/RepositoryRoundTripTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Training/TrainingUploadServiceTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Training/TrainingActivityQueryServiceTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Training/TrainingActivityDeletionServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Training/ParseTrainingJobHandlerTests.cs`

**Interfaces:**
- Produces: atomic `ITrainingUploadRepository.AcceptAsync(StoredUpload upload, DateTimeOffset now, CancellationToken) : TrainingUploadAcceptance`.
- Produces: training summary/detail/count projections and repository methods consumed by Task 5 and API Task 8.
- Produces: deletion result with successor rebuild job ID.

- [ ] **Step 1: Write failing upload/query/delete tests**

Require these behaviors:

```csharp
[Fact] public Task Accepted_upload_commits_upload_and_parse_job_together_and_returns_both_ids();
[Fact] public Task Duplicate_upload_returns_duplicate_without_partial_identifiers();
[Fact] public Task List_is_newest_first_and_does_not_load_sample_payloads();
[Fact] public Task Detail_exposes_quality_metadata_exclusions_and_reasons();
[Fact] public Task Delete_removes_activity_samples_and_source_upload_then_queues_rebuild();
[Fact] public Task Parse_handler_reports_every_stage_monotonically();
```

The PostgreSQL atomicity test must induce a job insert failure inside the repository transaction and assert the upload row rolls back.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~TrainingUploadServiceTests|FullyQualifiedName~TrainingActivityQueryServiceTests|FullyQualifiedName~TrainingActivityDeletionServiceTests|FullyQualifiedName~ParseTrainingJobHandlerTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~TrainingUploadRepositoryTests|FullyQualifiedName~RepositoryRoundTripTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: compile failures for new projections/repositories.

- [ ] **Step 3: Define exact persistence projections**

```csharp
public sealed record TrainingActivitySummary(
    Guid Id,
    Guid UploadId,
    TrainingActivityMetadata Metadata,
    TimeSpan MovingDuration,
    ActivityEligibility Eligibility,
    double PositionCoverage,
    double ElevationCoverage,
    double SpeedCoverage,
    double PowerCoverage,
    IReadOnlyList<string> ReasonCodes,
    DateTimeOffset CreatedAt);

public sealed record TrainingActivityDetail(
    TrainingActivitySummary Summary,
    IReadOnlyDictionary<string, int> ExclusionCounts);

public sealed record TrainingActivityCounts(int Total, int Eligible);

public interface ITrainingActivityRepository
{
    Task<Guid> SaveAsync(Guid uploadId, CleanedActivity activity, CancellationToken cancellationToken);
    Task<CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CleanedActivity>> GetAllAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TrainingActivitySummary>> GetSummariesAsync(CancellationToken cancellationToken);
    Task<TrainingActivityDetail?> GetDetailAsync(Guid activityId, CancellationToken cancellationToken);
    Task<TrainingActivityCounts> GetCountsAsync(CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid activityId, CancellationToken cancellationToken);
}
```

`DeleteAsync` uses one transaction to delete activity/samples and the associated retained FIT upload. Return false without mutation when absent.

- [ ] **Step 4: Implement atomic upload acceptance and bounded service reading**

```csharp
public sealed record TrainingUploadAcceptance(bool Accepted, Guid? UploadId, Guid? JobId);

public interface ITrainingUploadRepository
{
    Task<TrainingUploadAcceptance> AcceptAsync(
        StoredUpload upload,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public sealed record TrainingUpload(string FileName, Stream Content);
public sealed record TrainingUploadResult(
    string FileName,
    UploadOutcome Outcome,
    Guid? UploadId,
    Guid? JobId,
    string? ErrorCode);
```

The PostgreSQL implementation starts one transaction, performs `INSERT ... ON CONFLICT DO NOTHING` for the `fit` upload, creates a fully initialized `ParseTraining` job only when inserted, saves, and commits. A duplicate returns `Accepted=false` with both IDs null.

`TrainingUploadService` validates `.fit`, streams at most `50 * 1024 * 1024` bytes, rejects empty/oversized files as per-file `invalid`, hashes bytes, calls the repository, and maps accepted/duplicate results. Use `TimeProvider` for the upload and job timestamps.

Keep the still-inline `/api/training/uploads` handler compiling until Task 8 removes it: open each `IFormFile` stream into `TrainingUpload`, call the service before disposing those streams, and continue mapping only filename/outcome/error into the old three-field response. Do not expose the new route or IDs before the Task 7 contracts and Task 8 endpoint tests exist.

- [ ] **Step 5: Implement query and deletion services**

```csharp
public sealed class TrainingActivityQueryService(ITrainingActivityRepository activities)
{
    public Task<IReadOnlyList<TrainingActivitySummary>> GetSummariesAsync(CancellationToken ct) =>
        activities.GetSummariesAsync(ct);
    public Task<TrainingActivityDetail?> GetAsync(Guid id, CancellationToken ct) =>
        activities.GetDetailAsync(id, ct);
}

public sealed record TrainingActivityDeletionResult(bool Deleted, Guid? RebuildJobId);

public sealed class TrainingActivityDeletionService(
    ITrainingActivityRepository activities,
    IJobQueue jobs)
{
    public async Task<TrainingActivityDeletionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (!await activities.DeleteAsync(id, ct)) return new(false, null);
        var jobId = await jobs.EnqueueIfNotPendingAsync(JobType.BuildModel, ModelSubject.Id, ct);
        return new(true, jobId);
    }
}
```

The Task 3 queued-successor rule makes deletion during a running build eventually correct.

- [ ] **Step 6: Add parse progress stages**

Inject `IJobProgressReporter`. Report exactly `10/reading-upload`, `25/decoding-fit`, `50/cleaning-activity`, `75/saving-activity`, and `90/queueing-model-rebuild`. Keep progress before the operation it describes so a failure accurately identifies the active stage.

- [ ] **Step 7: Run focused tests and commit**

Run Step 2 commands, then:

```bash
git add src/RouteTimer.Services/Training src/RouteTimer.Services/Persistence src/RouteTimer.Persistence/Repositories src/RouteTimer.Api/Program.cs tests
git commit -m "feat: expose durable training workflows"
```

---

### Task 5: Add model readiness, coverage, rebuild, and build progress services

**Files:**
- Modify: `src/RouteTimer.Services/Jobs/IJobRepository.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/JobRepository.cs`
- Create: `src/RouteTimer.Services/Models/ModelStatusResult.cs`
- Create: `src/RouteTimer.Services/Models/ModelStatusService.cs`
- Create: `src/RouteTimer.Services/Models/ModelRebuildService.cs`
- Create: `src/RouteTimer.Services/Models/ModelRebuildRequestException.cs`
- Modify: `src/RouteTimer.Services/Models/BuildModelJobHandler.cs`
- Modify: `src/RouteTimer.Api/Program.cs` registrations only.
- Create: `tests/RouteTimer.Services.Tests/Models/ModelStatusServiceTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Models/ModelRebuildServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/BuildModelJobHandlerTests.cs`
- Test: `tests/RouteTimer.Persistence.Tests/Jobs/PostgresJobQueueTests.cs`

**Interfaces:**
- Produces: `ModelStatusResult` consumed by Task 8.
- Produces: `ModelRebuildService.RequestAsync(...) : Guid`.
- Adds job lookup `GetLatestAsync(JobType type, Guid subjectId, CancellationToken)`.

- [ ] **Step 1: Write failing readiness/rebuild/progress tests**

Cover: missing profile, no eligible evidence, building without current model, ready current model, ready current model while rebuilding, invalid persisted current model, latest failed rebuild warning, explicit rebuild prerequisite failures, coalesced job ID, and all six build progress stages.

Use this key assertion:

```csharp
Assert.True(status.IsReady);
Assert.NotNull(status.CurrentModel);
Assert.Equal(JobState.Running, status.RebuildJob!.State); // current model remains usable
```

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~ModelStatusServiceTests|FullyQualifiedName~ModelRebuildServiceTests|FullyQualifiedName~BuildModelJobHandlerTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

- [ ] **Step 3: Define exact service results**

```csharp
public sealed record ModelStatusResult(
    bool IsReady,
    string? BlockingReason,
    RiderModelSnapshot? CurrentModel,
    AnalysisJob? RebuildJob);

public sealed class ModelRebuildRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
```

`ModelStatusService` loads profile, activity counts, current model, and latest build job. Blocking precedence with no current model is: `profile-required`, `no-eligible-activities`, `model-building`, `model-build-failed`, `model-not-ready`. With a valid current model, always return `IsReady=true`; attach active/failed rebuild as status only. Translate `InvalidPersistedRiderModelException` to `invalid-rider-model` without leaking the inner message.

`ModelRebuildService` checks profile and `Eligible > 0`, throwing the same stable prerequisite codes, then calls `EnqueueIfNotPendingAsync(BuildModel, ModelSubject.Id)`.

- [ ] **Step 4: Add build progress reporting**

Inject `IJobProgressReporter` and report: `5/loading-evidence`, `20/building-power-model`, `40/calibrating-physics`, `55/building-descent-limits`, `70/validating-model`, `90/saving-model`. Do not alter fold isolation or model construction.

- [ ] **Step 5: Run tests and commit**

Run Step 2, the full Services test project, then:

```bash
git add src/RouteTimer.Services/Models src/RouteTimer.Services/Jobs src/RouteTimer.Persistence/Repositories src/RouteTimer.Api/Program.cs tests
git commit -m "feat: report rider model readiness"
```

---

### Task 6: Make prediction deletion and late publication race-safe

**Files:**
- Modify: `src/RouteTimer.Services/Persistence/IPredictionRepository.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/PredictionRepository.cs`
- Create: `src/RouteTimer.Services/Predictions/PredictionDeletionService.cs`
- Modify: `src/RouteTimer.Services/Predictions/PredictionJobHandler.cs`
- Modify: `src/RouteTimer.Api/Program.cs` registration only.
- Test: `tests/RouteTimer.Persistence.Tests/PredictionRepositoryTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionWorkflowTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Predictions/PredictionDeletionServiceTests.cs`

**Interfaces:**
- Replaces publication with owner-guarded `TryPublishAsync`.
- Adds atomic `DeleteAsync(Guid predictionId, DateTimeOffset now, CancellationToken) : bool`.

- [ ] **Step 1: Write failing cancellation/deletion/publication tests**

Use real PostgreSQL to prove:

```csharp
[Fact] public Task Delete_cancels_active_job_and_removes_segments_prediction_and_gpx_atomically();
[Fact] public Task Delete_keeps_referenced_immutable_rider_model();
[Fact] public Task Late_worker_cannot_publish_after_delete();
[Fact] public Task Publish_requires_matching_running_job_and_worker();
[Fact] public Task Missing_delete_returns_false_without_mutation();
```

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionRepositoryTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionWorkflowTests|FullyQualifiedName~PredictionDeletionServiceTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

- [ ] **Step 3: Finalize repository signatures**

```csharp
Task<bool> TryPublishAsync(
    Guid predictionId,
    Guid jobId,
    string workerId,
    PredictionPublication publication,
    CancellationToken cancellationToken);

Task<bool> DeleteAsync(
    Guid predictionId,
    DateTimeOffset now,
    CancellationToken cancellationToken);
```

`TryPublishAsync` starts a transaction and loads the prediction plus the exact `PredictRoute` job in `Running` state owned by `workerId`. Return false before changing segments when either is absent/mismatched. `DeleteAsync` cancels every queued/running matching job with Task 3 terminal semantics, deletes prediction/segments, then deletes the retained GPX only when no other prediction references it. Commit all mutations together.

- [ ] **Step 4: Add service and handler progress**

```csharp
public sealed class PredictionDeletionService(IPredictionRepository predictions, TimeProvider timeProvider)
{
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
        predictions.DeleteAsync(id, timeProvider.GetUtcNow(), ct);
}
```

Inject `IJobProgressReporter` into `PredictionJobHandler`; report `5/loading-prediction`, `20/processing-route`, `45/simulating-route`, `90/saving-result`. Call `await predictions.TryPublishAsync(prediction.Id, job.Id, job.WorkerId!, publication, cancellationToken)`; if it returns false, return normally so `AnalysisWorker.CompleteAsync` observes lost ownership and cannot revive cancellation.

- [ ] **Step 5: Run tests and commit**

Run Step 2 commands and full Persistence/Services projects, then:

```bash
git add src/RouteTimer.Services/Predictions src/RouteTimer.Services/Persistence src/RouteTimer.Persistence/Repositories src/RouteTimer.Api/Program.cs tests
git commit -m "feat: delete predictions safely"
```

---

### Task 7: Define final contracts and split API infrastructure

**Files:**
- Delete: `src/RouteTimer.Contracts/Class1.cs`
- Modify: `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`
- Replace: `src/RouteTimer.Contracts/Training/TrainingUploadContracts.cs`
- Create: `src/RouteTimer.Contracts/Training/TrainingActivityContracts.cs`
- Create: `src/RouteTimer.Contracts/Models/ModelContracts.cs`
- Create: `src/RouteTimer.Contracts/Uploads/UploadLimits.cs`
- Modify: `src/RouteTimer.Contracts/Jobs/JobResponse.cs`
- Modify: `src/RouteTimer.Contracts/Predictions/PredictionContracts.cs`
- Retain unchanged until Task 13: `src/RouteTimer.Contracts/Predictions/PredictionSubmissionContracts.cs` (the old client still references `PredictionRoutePreview`).
- Create: `src/RouteTimer.Api/Errors/ApiProblems.cs`
- Create: `src/RouteTimer.Api/Uploads/MultipartUploadReader.cs`
- Create: `src/RouteTimer.Api/Endpoints/ProfileEndpoints.cs`
- Create: `src/RouteTimer.Api/Endpoints/TrainingEndpoints.cs`
- Create: `src/RouteTimer.Api/Endpoints/ModelsEndpoints.cs`
- Create: `src/RouteTimer.Api/Endpoints/PredictionsEndpoints.cs`
- Create: `src/RouteTimer.Api/Endpoints/JobsEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Create: `tests/RouteTimer.Api.Tests/RouteTimerApiFactory.cs`
- Create: `tests/RouteTimer.Api.Tests/Endpoints/ProblemDetailsTests.cs`
- Modify: existing API tests to use the shared factory.

**Interfaces:**
- Freezes all DTO names consumed by Tasks 8–14.
- Endpoint files expose `Map*Endpoints(IEndpointRouteBuilder routes)` extension methods.

- [ ] **Step 1: Write failing contract/problem/infrastructure tests**

Assert camel-case DTO JSON, problem `code`, no worker ID, field-level profile validation with `invalid-profile`, malformed multipart `400`, bounded oversize `413`, fallback authorization, and that `Program.cs` maps resource modules rather than inline handlers.

- [ ] **Step 2: Run API tests and verify RED**

```bash
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ProblemDetailsTests|FullyQualifiedName~AuthorizationTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

- [ ] **Step 3: Add exact contract records**

```csharp
public sealed record TrainingUploadBatchResponse(IReadOnlyList<TrainingUploadFileResponse> Files);
public sealed record TrainingUploadFileResponse(string FileName, string Outcome, Guid? UploadId, Guid? JobId, string? ErrorCode);

public sealed record TrainingActivitySummaryResponse(
    Guid Id, Guid UploadId, string SourceFileName,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
    string? DeviceManufacturer, string? DeviceProduct,
    double? DistanceMetres, double? AscentMetres, double MovingSeconds,
    string Eligibility, double PositionCoverage, double ElevationCoverage,
    double SpeedCoverage, double PowerCoverage,
    IReadOnlyList<string> ReasonCodes, DateTimeOffset CreatedAt);

public sealed record TrainingActivityDetailResponse(
    TrainingActivitySummaryResponse Summary,
    IReadOnlyDictionary<string, int> ExclusionCounts);

public sealed record JobResponse(
    Guid Id, string Type, Guid SubjectId, string State,
    int ProgressPercent, string ProgressStage, int AttemptCount,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt,
    DateTimeOffset? LeaseExpiresAt, string? DiagnosticCode, string? DiagnosticMessage);

public sealed record PowerBandCoverageResponse(
    string GradeKey, string DurationKey, double TypicalWatts,
    double EvidenceSeconds, int ActivityCount, double ShrinkageWeight, string Confidence);

public sealed record PhysicalCoefficientsResponse(
    double DrivetrainEfficiency, double AirDensity, double RollingCoefficient, double CdA);

public sealed record ModelStatusResponse(
    bool IsReady, string? BlockingReason,
    Guid? ModelId, string? AlgorithmVersion, DateTimeOffset? CreatedAt,
    bool? WasCalibrated, bool? DescentWasLearned,
    string? ValidationStatus, double? ValidationMedianAbsolutePercentageError,
    double? ValidationP90AbsolutePercentageError,
    PhysicalCoefficientsResponse? PhysicalCoefficients,
    IReadOnlyList<PowerBandCoverageResponse> PowerBands,
    int LearnedDescentCellCount, int FallbackDescentCellCount,
    JobResponse? RebuildJob);

public sealed record ModelRebuildResponse(Guid JobId);
```

Keep existing prediction submission/summary/detail/segment names. `PredictionRoutePreview` remains temporarily for solution compatibility and is removed with its final client caller in Task 13. Add no domain types to Contracts.

Add the exact shared limits and stable error allowlist:

```csharp
public static class UploadLimits
{
    public const long MaximumFileBytes = 50L * 1024 * 1024;
    public const int MaximumTrainingFiles = 10;
    public const long MaximumTrainingRequestBytes = MaximumTrainingFiles * MaximumFileBytes + 1024 * 1024;
}

public static class ErrorCodes
{
    public const string MultipartRequired = "multipart-required";
    public const string InvalidProfile = "invalid-profile";
    public const string TooManyFiles = "too-many-files";
    public const string FitUploadRequired = "fit-upload-required";
    public const string InvalidFitUpload = "invalid-fit-upload";
    public const string FitTooLarge = "fit-too-large";
    public const string ActivityNotFound = "activity-not-found";
    public const string ProfileRequired = "profile-required";
    public const string NoEligibleActivities = "no-eligible-activities";
    public const string ModelNotReady = "model-not-ready";
    public const string InvalidRiderModel = "invalid-rider-model";
    public const string PredictionGpxRequired = "prediction-gpx-required";
    public const string InvalidGpxUpload = "invalid-gpx-upload";
    public const string GpxTooLarge = "gpx-too-large";
    public const string PredictionNotFound = "prediction-not-found";
    public const string JobNotFound = "job-not-found";
}
```

- [ ] **Step 4: Centralize problems and bounded multipart reading**

`ApiProblems.Create(status, code, detail)` must call `Results.Problem` with only safe fields and the `code` extension. Add typed helpers for `NotFound`, `Conflict`, `PayloadTooLarge`, and `BadRequest`, plus a validation helper that returns `HttpValidationProblemDetails` with the `invalid-profile` extension and only `riderWeightKg` and `bikeAndEquipmentWeightKg` field names.

`MultipartUploadReader.ReadAsync(HttpRequest, int minimumFileCount, int maximumFileCount, CancellationToken)` catches only boundary/form parsing exceptions, enforces the requested count range, and returns `IFormFileCollection`. File content remains streamed by services; do not copy arbitrary bodies in endpoint code. Configure `FormOptions.MultipartBodyLengthLimit` and Kestrel request size to `UploadLimits.MaximumTrainingRequestBytes`; service/API checks still enforce each file at `MaximumFileBytes`.

- [ ] **Step 5: Extract endpoint modules without changing successful behavior**

Move profile and existing prediction/job mappings first. Profile validation maps through the validation helper; do not return `ProfileValidationException.Message` as an unstructured body. `TrainingEndpoints` temporarily maps the existing `/api/training/uploads` behavior so the current authorization/regression tests remain green; it may serialize the new batch DTO but retains the old path and success status until Task 8 replaces it atomically. Before Task 8, `ModelsEndpoints` is the exact no-op extension `public static IEndpointRouteBuilder MapModelsEndpoints(this IEndpointRouteBuilder routes) => routes;`, so no fake `501` endpoint is exposed. `Program.cs` retains DI/auth/static/health composition and ends with:

```csharp
app.MapProfileEndpoints();
app.MapTrainingEndpoints();
app.MapModelsEndpoints();
app.MapPredictionEndpoints();
app.MapJobEndpoints();
app.Run();
```

- [ ] **Step 6: Run API and full solution tests, then commit**

```bash
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
git add src/RouteTimer.Contracts src/RouteTimer.Api tests/RouteTimer.Api.Tests
git commit -m "refactor: define step 9 API contracts"
```

---

### Task 8: Expose training and model resources

**Files:**
- Modify: `src/RouteTimer.Api/Endpoints/TrainingEndpoints.cs`
- Modify: `src/RouteTimer.Api/Endpoints/ModelsEndpoints.cs`
- Modify: `src/RouteTimer.Api/Errors/ApiProblems.cs`
- Create: `tests/RouteTimer.Api.Tests/Endpoints/TrainingEndpointTests.cs`
- Create: `tests/RouteTimer.Api.Tests/Endpoints/ModelEndpointTests.cs`
- Modify: `tests/RouteTimer.Api.Tests/Auth/AuthorizationTests.cs`

**Interfaces:**
- Completes `GET/POST/GET{id}/DELETE` training resources and `GET current/POST rebuild` model resources.

- [ ] **Step 1: Write failing authenticated endpoint tests**

Test exact paths/statuses: list/detail `200`, absent detail/delete `404`, mixed upload `202` with one response per file, oversized per-file invalid outcome without batch failure, delete `204` and rebuild job correlation, current model blocked/ready shapes, rebuild `202`, prerequisite `409`, anonymous `401`, non-rider `403`, and old `/api/training/uploads` `404`.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~TrainingEndpointTests|FullyQualifiedName~ModelEndpointTests|FullyQualifiedName~AuthorizationTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

- [ ] **Step 3: Map training resources**

`POST /api/training-activities` requires multipart, converts each `IFormFile` to `TrainingUpload(file.FileName, file.OpenReadStream())`, awaits `TrainingUploadService`, and returns `Results.Accepted("/api/training-activities", new TrainingUploadBatchResponse(...))`. Keep per-file invalid/duplicate outcomes in the response.

Map query projections field-for-field. DELETE returns `204` only when `Deleted=true`; the rebuild job is discoverable through `/api/models/current` and need not have a response body.

- [ ] **Step 4: Map model resources**

Map all current-model bands and coefficient/descent counts from `ModelStatusResult`. `POST /api/models/rebuild` returns `202` plus `ModelRebuildResponse`; map `profile-required` and `no-eligible-activities` to stable `409` problems.

- [ ] **Step 5: Run tests and commit**

Run Step 2 and the full API project, then:

```bash
git add src/RouteTimer.Api tests/RouteTimer.Api.Tests
git commit -m "feat: expose training and model resources"
```

---

### Task 9: Complete prediction and job resources

**Files:**
- Modify: `src/RouteTimer.Api/Endpoints/PredictionsEndpoints.cs`
- Modify: `src/RouteTimer.Api/Endpoints/JobsEndpoints.cs`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/PredictionEndpointTests.cs`
- Modify: `tests/RouteTimer.Api.Tests/Auth/AuthorizationTests.cs`

**Interfaces:**
- Completes prediction list/submission/detail/deletion and final lifecycle-rich job DTO.

- [ ] **Step 1: Extend failing API tests**

Add assertions for prediction deletion `204/404`, active-job cancellation, late detail `404`, ordered segments, segment-free summaries, `202` submission IDs, all job lifecycle/progress fields, no worker ID, stable malformed/size/prerequisite problems, and all auth cases.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionEndpointTests|FullyQualifiedName~AuthorizationTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

- [ ] **Step 3: Implement final mappings**

Keep submission streaming and `202 Accepted`. Map summary/detail using shared private mapper methods in `PredictionsEndpoints`; summaries must never include `Segments`. DELETE delegates only to `PredictionDeletionService`.

Map `JobResponse` field-for-field; emit lease expiry only while running and never emit `WorkerId`. Missing resources use code-bearing `404` problems (`prediction-not-found`, `job-not-found`) rather than empty `Results.NotFound()`.

- [ ] **Step 4: Run all API and full solution tests, then commit**

```bash
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
git add src/RouteTimer.Api tests/RouteTimer.Api.Tests
git commit -m "feat: complete prediction resource API"
```

---

### Task 10: Add the typed client, problem handling, polling, and shared status components

**Files:**
- Create: `src/RouteTimer.Client/Api/ClientFileUpload.cs`
- Create: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`
- Create: `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`
- Create: `src/RouteTimer.Client/Api/ApiProblemException.cs`
- Create: `src/RouteTimer.Client/Jobs/JobPoller.cs`
- Create: `src/RouteTimer.Client/Formatting/RouteTimerFormat.cs`
- Create: `src/RouteTimer.Client/Components/ProblemMessage.razor`
- Create: `src/RouteTimer.Client/Components/JobProgress.razor`
- Create: `src/RouteTimer.Client/Components/ModelStatus.razor`
- Create: `src/RouteTimer.Client/Components/ConfidenceBadge.razor`
- Modify: `src/RouteTimer.Client/Program.cs`
- Create: `tests/RouteTimer.Client.Tests/Api/RouteTimerApiClientTests.cs`
- Create: `tests/RouteTimer.Client.Tests/Jobs/JobPollerTests.cs`
- Create: `tests/RouteTimer.Client.Tests/Components/SharedStatusComponentTests.cs`
- Create: `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`
- Modify: `tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj`

**Interfaces:**
- Freezes the client boundary used by every page task.
- Produces deterministic polling outcome and shared formatting/status components.

- [ ] **Step 1: Write failing typed-client and poller tests**

Test every HTTP method/path, multipart filename, DTO deserialization, cancellation propagation, code-bearing problem parsing, safe fallback problem, immediate first poll, two-second intervals under `FakeTimeProvider`, two consecutive `404` removal, terminal stop, and caller cancellation.

Key poller test:

```csharp
[Fact]
public async Task Poller_requests_immediately_then_stops_on_success()
{
    var api = new FakeRouteTimerApiClient();
    api.Jobs.Enqueue(JobFixtures.Running(25, "processing-route"));
    api.Jobs.Enqueue(JobFixtures.Succeeded());
    var time = new FakeTimeProvider();
    var updates = new List<JobResponse>();
    var task = new JobPoller(api, time).PollAsync(Guid.NewGuid(),
        job => { updates.Add(job); return Task.CompletedTask; }, CancellationToken.None);

    await Task.Yield();
    Assert.Single(updates);
    time.Advance(TimeSpan.FromSeconds(2));
    Assert.Equal(JobPollOutcome.Succeeded, await task);
}
```

- [ ] **Step 2: Run client tests and verify RED**

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~RouteTimerApiClientTests|FullyQualifiedName~JobPollerTests|FullyQualifiedName~SharedStatusComponentTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

- [ ] **Step 3: Define the exact client interface**

```csharp
public sealed record ClientFileUpload(string FileName, long Size, Func<Stream> OpenReadStream);

public interface IRouteTimerApiClient
{
    Task<ProfileResponse?> GetProfileAsync(CancellationToken ct);
    Task<ProfileResponse> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct);
    Task<IReadOnlyList<TrainingActivitySummaryResponse>> GetTrainingActivitiesAsync(CancellationToken ct);
    Task<TrainingActivityDetailResponse?> GetTrainingActivityAsync(Guid id, CancellationToken ct);
    Task<TrainingUploadBatchResponse> UploadTrainingActivitiesAsync(IReadOnlyList<ClientFileUpload> files, CancellationToken ct);
    Task<bool> DeleteTrainingActivityAsync(Guid id, CancellationToken ct);
    Task<ModelStatusResponse> GetModelStatusAsync(CancellationToken ct);
    Task<ModelRebuildResponse> RebuildModelAsync(CancellationToken ct);
    Task<IReadOnlyList<PredictionSummaryResponse>> GetPredictionsAsync(CancellationToken ct);
    Task<PredictionSubmissionResponse> SubmitPredictionAsync(ClientFileUpload file, CancellationToken ct);
    Task<PredictionDetailResponse?> GetPredictionAsync(Guid id, CancellationToken ct);
    Task<bool> DeletePredictionAsync(Guid id, CancellationToken ct);
    Task<JobResponse?> GetJobAsync(Guid id, CancellationToken ct);
}
```

`RouteTimerApiClient` is the only production type that builds URLs or touches `HttpResponseMessage`. Open and dispose upload streams inside the method. Read at most safe problem fields; map `404` GET/DELETE to null/false and throw `ApiProblemException` for other non-success responses.

`ApiProblemException` exposes `StatusCode`, stable `Code`, safe `Title`, safe `Detail`, and an `IReadOnlyDictionary<string, string[]> Errors`. Parse only those RFC problem fields, cap each displayed string at 512 characters, use code `request-failed` when the body is absent/malformed, and never retain the raw response body.

- [ ] **Step 4: Implement deterministic polling**

```csharp
public enum JobPollOutcome { Succeeded, Failed, Cancelled, Removed }

public sealed class JobPoller(IRouteTimerApiClient api, TimeProvider timeProvider)
{
    public async Task<JobPollOutcome> PollAsync(
        Guid jobId,
        Func<JobResponse, Task> onUpdate,
        CancellationToken cancellationToken);
}
```

Implementation: fetch immediately; count consecutive null responses; return `Removed` on the second; invoke `onUpdate` for every non-null response; map exact terminal state names case-insensitively; otherwise `await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken)`.

- [ ] **Step 5: Implement shared components and formatting**

`RouteTimerFormat` exposes pure methods: `Distance(double?)`, `Ascent(double?)`, `Duration(double?)`, `Speed(double?)`, `Power(double?)`, `Weight(double?)`, `Percentage(double?)`, and `Timestamp(DateTimeOffset?)`. Use invariant metric symbols and current culture numeric formatting; null returns `—`.

Components accept DTOs/strings as `[Parameter]` values and contain no API calls. `JobProgress` maps every stable stage code to readable text and renders `<progress max="100">` plus text. `ConfidenceBadge` renders text plus class, never color alone. `ProblemMessage` accepts `ApiProblemException?` and a fallback message with `role="alert"`.

- [ ] **Step 6: Register and verify**

Add `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />` to the client test project. Register `IRouteTimerApiClient` scoped from the authorized `HttpClient`, `JobPoller` scoped, and `TimeProvider.System` singleton. Run Step 2 plus the full client project.

- [ ] **Step 7: Commit Task 10**

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: add typed RouteTimer client"
```

---

### Task 11: Build dashboard and profile workflows

**Files:**
- Modify: `src/RouteTimer.Client/Pages/Home.razor`
- Modify: `src/RouteTimer.Client/Pages/Home.razor.css`
- Modify: `src/RouteTimer.Client/Pages/Profile.razor`
- Create: `src/RouteTimer.Client/Pages/Profile.razor.css`
- Modify: `src/RouteTimer.Client/Layout/MainLayout.razor`
- Modify: `src/RouteTimer.Client/Layout/MainLayout.razor.css`
- Modify: `src/RouteTimer.Client/Layout/NavMenu.razor`
- Delete: `src/RouteTimer.Client/Pages/Counter.razor`
- Delete: `src/RouteTimer.Client/Pages/Weather.razor`
- Delete: `src/RouteTimer.Client/wwwroot/sample-data/weather.json`
- Modify: `tests/RouteTimer.Client.Tests/DashboardTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/ProfilePageTests.cs`

**Interfaces:**
- Consumes only Task 10 client/components and Task 7 contracts.

- [ ] **Step 1: Write failing dashboard/profile component tests**

Dashboard tests cover independent loading/failure sections, missing-profile action, eligible/total counts, ready/building/failed model display, validation target and median/p90 values, active rebuild progress, recent prediction links, and no confidence percentages.

Profile tests cover loading existing values, `30..250` and `3..60` boundaries, disabled invalid/duplicate submission, successful save, server field problem, network problem, and disposal cancellation.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~DashboardTests|FullyQualifiedName~ProfilePageTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

- [ ] **Step 3: Implement dashboard state per section**

Inject `IRouteTimerApiClient`. Start profile, training, model, and prediction requests together. Await/capture each separately so one `ApiProblemException` does not blank other cards. Render explicit loading, empty, warning, failure, and success markup with stable `data-testid` values used by tests. Show at most five recent predictions.

- [ ] **Step 4: Implement profile form state**

Use `EditForm` with a small page model and `DataAnnotations` exact ranges. Track the last saved value to disable duplicate saves. Use a component-owned `CancellationTokenSource`, cancel/dispose it, and never catch `OperationCanceledException` as a visible error. Keep server problems in `ProblemMessage`.

- [ ] **Step 5: Replace template shell residue**

Remove the Microsoft “About” link, the template counter/weather routes, and their sample JSON. Keep four navigation destinations, add an authenticated account/logout area using existing OIDC components, and ensure the main landmark/page title/focus behavior remains accessible. Do not redesign authentication.

- [ ] **Step 6: Run client/full tests and commit**

Run Step 2, full Client tests, then full solution. Commit:

```bash
git add src/RouteTimer.Client/Layout src/RouteTimer.Client/Pages src/RouteTimer.Client/wwwroot/sample-data tests/RouteTimer.Client.Tests
git commit -m "feat: show rider dashboard and profile"
```

---

### Task 12: Build training upload, list, detail, progress, and deletion UI

**Files:**
- Modify: `src/RouteTimer.Client/Pages/Training.razor`
- Create: `src/RouteTimer.Client/Pages/Training.razor.css`
- Create: `src/RouteTimer.Client/Pages/TrainingDetail.razor`
- Create: `src/RouteTimer.Client/Pages/TrainingDetail.razor.css`
- Delete: `tests/RouteTimer.Client.Tests/UploadPageTests.cs` after moving its FIT input assertion into `TrainingPageTests.cs`.
- Create: `tests/RouteTimer.Client.Tests/TrainingPageTests.cs`
- Create: `tests/RouteTimer.Client.Tests/TrainingDetailPageTests.cs`

**Interfaces:**
- Consumes Task 10 client/poller, training contracts, `JobProgress`, and formatting helpers.

- [ ] **Step 1: Write failing training page tests**

Cover `.fit`/multiple attributes, no-file state, per-file accepted/duplicate/invalid outcomes, accepted job polling, page-disposal cancellation, newest-first activity list, eligibility/reason text, empty/failure state, detail link, delete confirmation text, cancel delete, confirmed deletion refresh, and rebuild-status refresh.

Detail tests cover every summary field, `Unavailable` optional metadata, coverage percentages, sorted exclusion counts/reasons, `404` guidance, loading/failure, and no sample payload/table.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~TrainingPageTests|FullyQualifiedName~TrainingDetailPageTests|FullyQualifiedName~UploadPageTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

- [ ] **Step 3: Implement upload/list/polling state**

Call `args.GetMultipleFiles(UploadLimits.MaximumTrainingFiles)` and map a count overflow to visible `too-many-files` guidance. Convert each accepted `IBrowserFile` to:

```csharp
new ClientFileUpload(file.Name, file.Size,
    () => file.OpenReadStream(50L * 1024 * 1024, pageCancellation.Token))
```

Render every `TrainingUploadFileResponse` independently. Start one poll per accepted `JobId`, retain status keyed by job ID, and refresh activities/model after terminal parse/rebuild. Prevent a second upload while one is submitting; selection may contain invalid extensions but the API outcome remains authoritative.

- [ ] **Step 4: Implement deletion and detail**

Use an inline confirmation region rather than browser `confirm()`. Required text: “Deleting this activity removes its retained training evidence and queues a new rider-model build. Historical predictions will not change.” Disable other delete actions while awaiting. After `204`, remove the item immediately, refresh model status, and poll any rebuild surfaced there.

The detail route uses `@page "/training/{Id:guid}"`, fetches once, and renders dictionaries ordered by key for deterministic UI/tests.

- [ ] **Step 5: Run tests and commit**

Run Step 2 and full Client tests, then:

```bash
git add src/RouteTimer.Client/Pages/Training* tests/RouteTimer.Client.Tests
git commit -m "feat: add training activity interface"
```

---

### Task 13: Build prediction submission, durable history, progress, and textual detail

**Files:**
- Modify: `src/RouteTimer.Client/Pages/Predictions.razor`
- Create: `src/RouteTimer.Client/Pages/Predictions.razor.css`
- Create: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Create: `src/RouteTimer.Client/Pages/PredictionDetail.razor.css`
- Create: `tests/RouteTimer.Client.Tests/PredictionsPageTests.cs`
- Create: `tests/RouteTimer.Client.Tests/PredictionDetailPageTests.cs`
- Delete: `src/RouteTimer.Contracts/Predictions/PredictionSubmissionContracts.cs`

**Interfaces:**
- Produces stable detail markup and ordered segment input consumed by Task 14 visualization.

- [ ] **Step 1: Write failing prediction workflow tests**

Cover model/profile blocking guidance, one `.gpx` selection, consumption of `PredictionSubmissionResponse`, job polling and terminal navigation, refresh-safe history newest first, queued/running/failed/cancelled rows, formatted summaries, delete confirmation/cancel/confirm, empty/failure states, and disposal cancellation.

Detail tests cover every stored summary/snapshot/assumption/validation/confidence/warning field, metric formatting, pending/failed state, ordered segment handoff, missing resource, and the rule that visualization markup appears only for succeeded non-empty segments.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionsPageTests|FullyQualifiedName~PredictionDetailPageTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

- [ ] **Step 3: Implement submission/history**

Load model status and prediction history independently. Disable GPX submission unless model is ready. Convert the selected file with the same 50 MiB client guard, call `SubmitPredictionAsync`, add a queued row immediately, poll the returned job, and navigate with `NavigationManager.NavigateTo($"/predictions/{response.PredictionId}")` only on success. Failed/cancelled jobs remain in history after refresh.

Use inline deletion confirmation: “Deleting this prediction removes its retained GPX and result. Training data and rider models will not change.”

Delete `PredictionSubmissionContracts.cs` in this task after `Predictions.razor` no longer references `PredictionRoutePreview`; `PredictionSubmissionResponse` remains in `PredictionContracts.cs`.

- [ ] **Step 4: Implement textual detail**

Use `@page "/predictions/{Id:guid}"`. Render snapshot values from the response only; never query the current profile/model to decorate historical detail. Sort segments by sequence defensively before passing them to Task 14 components, but display a warning if incoming order differed so the API regression remains visible in tests.

- [ ] **Step 5: Run tests and commit**

Run Step 2 and full Client tests, then:

```bash
git add src/RouteTimer.Contracts/Predictions src/RouteTimer.Client/Pages/Predictions* src/RouteTimer.Client/Pages/PredictionDetail* tests/RouteTimer.Client.Tests
git commit -m "feat: add durable prediction interface"
```

---

### Task 14: Add locally bundled synchronized map and profile visualization

**Files:**
- Create: `src/RouteTimer.Client/package.json`
- Create: `src/RouteTimer.Client/package-lock.json`
- Create: `src/RouteTimer.Client/scripts/build-vendor.mjs`
- Create: `src/RouteTimer.Client/wwwroot/js/route-visualization-core.mjs`
- Create: `src/RouteTimer.Client/wwwroot/js/route-visualization.js`
- Create: `src/RouteTimer.Client/wwwroot/js/route-visualization.test.mjs`
- Create: `src/RouteTimer.Client/Components/RouteMap.razor`
- Create: `src/RouteTimer.Client/Components/RouteProfiles.razor`
- Create: `src/RouteTimer.Client/Components/PredictionVisualization.razor`
- Create: `src/RouteTimer.Client/Components/PredictionVisualization.razor.css`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Modify: `src/RouteTimer.Client/wwwroot/index.html`
- Modify: `src/RouteTimer.Client/wwwroot/appsettings.json`
- Modify: `Dockerfile`
- Create: `tests/RouteTimer.Client.Tests/PredictionVisualizationTests.cs`

**Interfaces:**
- JS exports: `initializeMap`, `initializeProfiles`, `selectMapSequence`, `selectProfileSequence`, `disposeMap`, `disposeProfiles`.
- Pure core exports: `buildProfileDatasets`, `nearestSegmentSequence`, `normalizeSegments`.
- Components exchange only segment sequence integers.

- [ ] **Step 1: Create locked package metadata**

Use exactly:

```json
{
  "name": "routetimer-client-assets",
  "private": true,
  "type": "module",
  "scripts": {
    "build:vendor": "node scripts/build-vendor.mjs",
    "test": "node --test wwwroot/js/route-visualization.test.mjs"
  },
  "dependencies": {
    "chart.js": "4.5.1",
    "leaflet": "1.9.4"
  }
}
```

Run `npm install --package-lock-only` in `src/RouteTimer.Client` and commit the resulting lockfile.

- [ ] **Step 2: Write failing pure JS and bUnit tests**

Node tests cover raw-to-display datasets (distance km, gradient percent, speed km/h), empty/non-finite rejection, nearest sequence, equal-distance lower-sequence tie, and stable ordering. bUnit/fake-JS tests cover successful initialization only with segments/config, callbacks in both directions, textual selected metrics, re-render selection, and disposal of map, profiles, and `DotNetObjectReference`.

```bash
cd src/RouteTimer.Client && npm test
dotnet test ../../tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionVisualizationTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: RED because modules/components do not exist.

- [ ] **Step 3: Build deterministic vendor assets**

`build-vendor.mjs` deletes only its exact output directory and copies:

- `node_modules/leaflet/dist/leaflet.js`;
- `node_modules/leaflet/dist/leaflet.css`;
- `node_modules/leaflet/dist/images/*`;
- `node_modules/chart.js/dist/chart.umd.js`.

Use Node `fs/promises`; no shell-specific copy command. Output under `wwwroot/vendor/{leaflet,chart.js}`. Keep output gitignored.

- [ ] **Step 4: Implement pure helpers and interop lifecycle**

`normalizeSegments` rejects missing/non-finite coordinates, elevations, distance, gradient, power, or speed and sorts by sequence. `nearestSegmentSequence` compares squared latitude/longitude distance and resolves ties to lower sequence. `buildProfileDatasets` returns four named datasets with exact labels `Elevation`, `Gradient`, `Power`, `Speed`.

`route-visualization.js` keeps separate `Map` registries keyed by component ID. Initialization first disposes an existing same-ID handle. Map click invokes `OnSequenceSelected`; chart hover does likewise. Selection functions move exactly one marker/cursor. Dispose removes Leaflet map/listeners, destroys all Chart instances, deletes registry entries, and is idempotent.

- [ ] **Step 5: Implement Blazor components and configuration**

`PredictionVisualization` owns selected sequence and coordinates child EventCallbacks. Each child imports the JS module in `OnAfterRenderAsync`, owns/disposes its own `DotNetObjectReference`, and exposes `SelectAsync(int sequence)`. Configuration keys are:

```json
"MapTiles": {
  "Url": "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
  "Attribution": "&copy; OpenStreetMap contributors"
}
```

Missing URL/attribution renders `ProblemMessage` and skips interop. Add Leaflet CSS and local UMD script tags before Blazor in `index.html`; no CDN.

- [ ] **Step 6: Add Node vendor stage to Docker build**

Add a first `node:22-alpine` stage that copies package files and scripts, runs `npm ci && npm run build:vendor`, then copy `/client/wwwroot/vendor` into `src/RouteTimer.Client/wwwroot/vendor` in the existing .NET build stage before client publish. Do not otherwise alter Compose/deployment topology.

- [ ] **Step 7: Run asset, JS, client, and Docker checks**

```bash
cd src/RouteTimer.Client
npm ci
npm run build:vendor
npm test
cd ../..
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
docker build -t routetimer:step9 .
rg -n "unpkg|jsdelivr|cdnjs" src/RouteTimer.Client --glob '!package-lock.json'
```

Expected: all tests/builds PASS; final `rg` emits no runtime source matches.

- [ ] **Step 8: Commit Task 14**

```bash
git add Dockerfile src/RouteTimer.Client/package.json src/RouteTimer.Client/package-lock.json src/RouteTimer.Client/scripts src/RouteTimer.Client/wwwroot src/RouteTimer.Client/Components src/RouteTimer.Client/Pages/PredictionDetail.razor tests/RouteTimer.Client.Tests
git commit -m "feat: visualize route predictions"
```

---

### Task 15: Final acceptance, handoff update, and review gate

**Files:**
- Modify only files required by review findings.
- Modify: `work-left-to-do.md` after all checks/review are clean.
- Review: both authoritative specs and every file changed since the plan commit.

**Interfaces:**
- Consumes every Task 1–14 deliverable.
- Produces a clean review-ready `codex/step-9` branch; no Step 10 implementation.

- [ ] **Step 1: Verify required API surface and obsolete-route removal**

```bash
rg -n 'Map(Get|Post|Put|Delete)\("/api' src/RouteTimer.Api
rg -n '/api/training/uploads|PredictionRoutePreview|@inject HttpClient' src tests
```

Expected: all spec resources present; second command emits no production/test caller matches.

- [ ] **Step 2: Verify browser assets and extracted examples discipline**

```bash
unzip -q -o Examples.zip
test -f RouteTimerExamples/23940033376_ACTIVITY.fit
test -f RouteTimerExamples/Station-Approach-Westbury-BA13-4HP-UK-to-A303-Salisbury-SP4-7DE-UK.gpx
git status --short --untracked-files=all
cd src/RouteTimer.Client && npm ci && npm run build:vendor && npm test && cd ../..
rg -n "unpkg|jsdelivr|cdnjs" src/RouteTimer.Client --glob '!package-lock.json'
```

Expected: examples exist but remain ignored; npm checks pass; no CDN matches.

- [ ] **Step 3: Run clean full verification**

```bash
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet format RouteTimer.slnx --no-restore --verify-no-changes --severity error
git diff --check
dotnet ef migrations has-pending-model-changes --project src/RouteTimer.Persistence/RouteTimer.Persistence.csproj --startup-project src/RouteTimer.Api/RouteTimer.Api.csproj
docker build -t routetimer:step9 .
```

Expected: all discovered .NET/Node tests pass, formatter/diff are silent, EF reports no changes, Docker build succeeds.

- [ ] **Step 4: Run acceptance matrix manually from automated evidence**

Record evidence for each spec acceptance criterion: training upload/quality/delete, job lifecycle, current model status/rebuild, profile validation, prediction submission/history/delete, stored snapshot detail, map/four profiles/synchronization/disposal, auth/problems, PostgreSQL concurrency, and Step 10 deferrals. Do not claim authenticated browser acceptance; it is explicitly Step 10.

- [ ] **Step 5: Request whole-branch review**

Use `superpowers:requesting-code-review`. Review range is the plan commit through `HEAD`. Require findings to include severity, exact file/line, violated spec paragraph, reproduction/test evidence, and whether it blocks Step 9. The reviewer must explicitly inspect migration downgrade, legacy rows, coalescing races, cancellation/publication races, problem safety, page disposal, and JS handle disposal.

- [ ] **Step 6: Address review findings with focused tests**

For each accepted blocker: add/adjust a failing test, verify RED, apply the smallest fix, verify GREEN, then rerun Step 3. Commit fixes separately:

```bash
git add src tests Dockerfile
git commit -m "fix: address step 9 review findings"
```

Do not create an empty fix commit.

- [ ] **Step 7: Update the remaining-work handoff**

Change `work-left-to-do.md` to state Steps 1–9 are complete and leave only Step 10. Remove Step 9 limitations that are demonstrably closed; retain the no-EndToEnd-tests limitation until Step 10 adds them. Commit:

```bash
git add work-left-to-do.md
git commit -m "docs: mark step 9 complete"
```

- [ ] **Step 8: Verify final clean head and prepare integration**

Rerun Step 3 after the documentation commit, then:

```bash
git status --short
git log --oneline --decorate main..HEAD
```

Expected: empty status and one reviewed commit sequence per task. Use `superpowers:finishing-a-development-branch`; do not push, merge, delete the worktree, or delete the branch before the user chooses an integration option.

---

## Spec Coverage Matrix

| Step 9 design section | Implementing task(s) | Primary verification |
|---|---:|---|
| Training metadata | 1–2 | parser/cleaner tests, PostgreSQL legacy/round-trip tests |
| Durable job progress/lifecycle | 2–3 | queue concurrency and worker tests |
| Coalesced rebuild correctness | 2–5 | partial-index migration and concurrent successor tests |
| Training resource services | 4 | service and repository atomicity tests |
| Model readiness/rebuild | 5 | readiness matrix and handler progress tests |
| Prediction deletion/publication race | 6 | PostgreSQL cancellation/delete/late-publish tests |
| API surface/contracts/problems | 7–9 | authenticated endpoint and problem-shape tests |
| Typed client and polling | 10 | HTTP boundary and fake-time polling tests |
| Dashboard/profile | 11 | bUnit state, validation, and failure-isolation tests |
| Training pages | 12 | bUnit upload/list/detail/delete/poll tests |
| Prediction pages | 13 | bUnit submission/history/detail/delete tests |
| Route visualization | 14 | Node pure-helper tests, bUnit interop lifecycle tests, Docker vendor build |
| Accessibility/presentation states | 10–14 | semantic bUnit assertions and textual state coverage |
| Complete verification/Step 10 deferral | 15 | acceptance matrix, full commands, whole-branch review |

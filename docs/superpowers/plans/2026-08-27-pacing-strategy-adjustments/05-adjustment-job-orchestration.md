[← Back to plan overview](README.md)

# Task 5: Add durable adjustment creation, query, deletion, and job orchestration

**Files:**

- Modify: `src/RouteTimer.Domain/Jobs/AnalysisJob.cs`
- Create: `src/RouteTimer.Services/Adjustments/IPacingStrategyHandler.cs`
- Create: `src/RouteTimer.Services/Adjustments/PacingStrategyDispatcher.cs`
- Create: `src/RouteTimer.Services/Adjustments/PredictionAdjustmentService.cs`
- Create: `src/RouteTimer.Services/Adjustments/PredictionAdjustmentQueryService.cs`
- Create: `src/RouteTimer.Services/Adjustments/PredictionAdjustmentDeletionService.cs`
- Create: `src/RouteTimer.Services/Adjustments/PredictionAdjustmentJobHandler.cs`
- ~~Modify: `src/RouteTimer.Api/Workers/AnalysisWorker.cs`~~ not needed (see note below)
- Modify: `src/RouteTimer.Api/Program.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/PredictionAdjustmentWorkflowTests.cs`
- ~~Modify: `tests/RouteTimer.Api.Tests/Workers/AnalysisWorkerTests.cs`~~ not needed (see note below)

**Implementation notes (deviations from plan):**

- **`AnalysisWorker` needed no change.** It already dispatches by matching `job.Type` against every
  registered `IJobHandler.Handles` (`handlers.FirstOrDefault(candidate => candidate.Handles == job.Type)`)
  — fully generic, with no per-job-type switch to extend. Registering
  `AddScoped<IJobHandler, PredictionAdjustmentJobHandler>()` in `Program.cs` is the only wiring needed.
  Its own test file needed no change either: `AnalysisWorkerTests.cs` already parameterizes every test
  by an arbitrary `JobType` via `FakeJobHandler(JobType, ...)`, so the existing `ParseTraining`/
  `BuildModel`/`PredictRoute` cases already prove the generic dispatch path works for any type.
- **`IPacingStrategyHandler` grew three methods beyond the design's `Run`-only interface**:
  `Canonicalize`, `Deserialize`, and `CanonicalizeReport`. Per [Task 3](03-adjustment-domain-contracts.md)'s
  deviation, no concrete `PacingStrategyDefinition`/`PacingStrategyReport` subtypes exist yet, so nothing
  can canonicalize or deserialize a strategy's JSON generically the way `PacingStrategyJson.Canonicalize<T>`
  requires a compile-time-known `T`. Since each strategy's own handler is the only place that *does* know
  its own concrete type, the handler owns serialization for its own strategy instead of a shared
  polymorphic root — `PredictionAdjustmentService` and `PredictionAdjustmentJobHandler` never need to know
  a concrete type. `PacingStrategyDispatcher` gained `TryGetHandlerForCreation`/`GetHandlerForProcessing`
  (rather than a single lookup) so a disabled strategy blocks new adjustments (creation lookup respects
  the enabled set) without stranding an already-queued job for that strategy (processing lookup does not).
- **`PredictionAdjustmentJobHandler`'s step order differs from the design's numbered list**: it maps the
  baseline's persisted segments to a `PredictionRoute` *before* resolving the strategy handler and
  deserializing (the design lists "map ordered persisted baseline segments" as step 5 and "deserialize...
  dispatch" as steps 6-7, which is the order implemented — a malformed/corrupt persisted baseline fails
  with `invalid-prediction-adjustment-result` before ever touching the dispatcher, which also makes it
  independent of whether any handler is registered for the strategy at all).
- **`PacingStrategyDispatcher` is registered with an empty handler list and an empty enabled-types list**
  in `Program.cs` for now, since no concrete handlers exist yet (nothing to register, nothing that could
  be enabled). Task 6 replaces the literal `enabledTypes: []` with the set derived from
  `PacingStrategyOptions`, and each strategy's delivery task (8, 10, 11, 12, 13) adds its own handler
  registration.

**Step 1: Add failing workflow tests**

Test creation order as one operation: validate enabled strategy and baseline, canonicalize, insert queued child, enqueue `AdjustPrediction` with `SubjectId == adjustment.Id`, and return both IDs. Test cleanup if enqueue fails. Test job retry/idempotency, cancellation, progress stages, stale lease publication, missing captured model, and malformed persisted baseline segments.

Add a dispatcher construction test that fails on duplicate handlers or a missing enabled handler.

**Step 2: Add `JobType.AdjustPrediction` and route it in the worker**

Use the existing handler scope and retry policy. Progress stages are stable strings:

```text
LoadingBaseline -> PreparingStrategy -> Simulating -> Publishing -> Complete
```

The job subject is the adjustment ID, never the baseline ID.

**Step 3: Reconstruct the immutable context**

The job handler must:

1. load the adjustment with its baseline owner;
2. load the baseline detail and require `Succeeded`;
3. load the exact `RiderModelId` captured by the baseline;
4. use the baseline profile and assumptions snapshots;
5. map ordered persisted baseline segments to `PredictionRoute`;
6. deserialize the canonical definition to the stored discriminator;
7. dispatch exactly one handler; and
8. publish only through the owner/job/worker guarded transaction.

Do not call the GPX parser and do not read the current profile or latest model.

**Step 4: Make deletion cancellation-safe**

Deleting an adjustment cancels queued/running jobs for that adjustment before deleting the row, in one repository transaction where possible. Deleting a baseline must continue to cancel its own prediction job and additionally cancel all active child adjustment jobs before cascade deletion.

**Step 5: Register services and validate startup**

Register one handler collection, the dispatcher, lifecycle services, repository, and job handler. At startup, compare enabled strategy types with registered handler types and fail on missing/duplicate registrations.

**Step 6: Run workflow and worker tests, then commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~PredictionAdjustment -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter FullyQualifiedName~AnalysisWorker -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Domain/Jobs src/RouteTimer.Services/Adjustments src/RouteTimer.Api/Workers src/RouteTimer.Api/Program.cs tests/RouteTimer.Services.Tests/Adjustments tests/RouteTimer.Api.Tests/Workers
git commit -m "feat: orchestrate prediction adjustment jobs"
```

**Step 7: Push and summarize**

```bash
git push
```

Summarize the change for this task: the durable job lifecycle, the immutable-context reconstruction rules, cancellation-safe deletion, and startup validation. Note any retry/idempotency edge case a reviewer should re-check.

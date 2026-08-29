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
- Modify: `src/RouteTimer.Api/Workers/AnalysisWorker.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/PredictionAdjustmentWorkflowTests.cs`
- Modify: `tests/RouteTimer.Api.Tests/Workers/AnalysisWorkerTests.cs`

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

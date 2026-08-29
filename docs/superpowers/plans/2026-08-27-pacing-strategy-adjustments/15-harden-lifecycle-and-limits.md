[← Back to plan overview](README.md)

# Task 15: Harden lifecycle, limits, and backward compatibility

**Files:**

- Modify: `src/RouteTimer.Services/Adjustments/PredictionAdjustmentService.cs`
- Modify: `src/RouteTimer.Services/Adjustments/PredictionAdjustmentJobHandler.cs`
- Modify: `src/RouteTimer.Services/Predictions/PredictionDeletionService.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/PredictionAdjustmentRepository.cs`
- Modify: `src/RouteTimer.Api/Endpoints/PredictionAdjustmentEndpoints.cs`
- Modify: `src/RouteTimer.Api/Errors/ApiProblems.cs`
- Modify: `src/RouteTimer.Api/appsettings.json`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/PredictionAdjustmentFailureTests.cs`
- Test: `tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs`
- Test: `tests/RouteTimer.Persistence.Tests/PredictionAdjustmentRepositoryTests.cs`
- Modify: existing prediction deletion, endpoint, and workflow tests.

**Step 1: Add adversarial tests**

Test parallel sibling creation, duplicate delivery of the same job, delete while queued, delete while running, baseline delete while multiple children run, worker lease expiry, cancellation during search, all-candidates-invalid, malformed persisted strategy JSON, unknown stored discriminator, unknown warning, oversized payload by UTF-8 bytes, and cross-baseline ID probing.

**Step 2: Enforce resource bounds**

Apply definition-size and list limits before enqueue and again after deserialization in the worker. Ensure each search has fixed coarse-grid size, tolerance, and maximum evaluations. Do not add an implicit sibling-retention cap: the approved model allows any number of append-only children, and a future operational limit would require a separate product decision.

**Step 3: Protect baseline APIs**

Run the existing prediction endpoint snapshots and confirm no baseline contract gained strategy, adjustment, or export fields. Verify existing predictions created before the migration can create an adjustment from persisted segments.

**Step 4: Run service, persistence, and API projects**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

**Step 5: Commit**

```bash
git add src tests
git commit -m "test: harden pacing adjustment lifecycle"
```

**Step 6: Push and summarize**

```bash
git push
```

Summarize the change for this task: the adversarial scenarios now covered, the resource bounds enforced at both enqueue and worker deserialization, and confirmation that baseline API compatibility holds. Note anything about the double-checkpointed validation (client, enqueue, worker) a reviewer should verify.

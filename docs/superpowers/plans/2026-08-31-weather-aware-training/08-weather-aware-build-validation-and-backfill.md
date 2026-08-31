[← Plan overview](README.md)

# Weather-Aware Build, Validation, and Backfill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Activate weather enrichment for new/existing rides and publish rider models only from complete weather-ready evidence.

**Architecture:** Build orchestration maps persisted Ready evidence into weather timelines/resolved activities, gates while eligible evidence is Pending, and runs power/calibration/descent/validation in dependency order. Parsing queues enrichment; a background reconciler idempotently queues existing/stale rides after migrations.

**Tech Stack:** Existing analysis jobs/worker, hosted background service, model services, xUnit/API host tests.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Keep the current rider model active until a complete successor model is saved.
- Never publish a model while any otherwise-eligible activity is Pending.
- Failed/Unavailable eligible rides are counted and excluded; Ready eligible rides build normally.
- Power uses raw `CleanedActivity`; physics/descent/validation use weather-resolved evidence.
- New uploads queue `EnrichTrainingWeather`, never `BuildModel` directly.

### Task 8: Switch the model pipeline and activate reconciliation

**Files:**

- Create: `src/RouteTimer.Services/Weather/WeatherActivityEvidence.cs`
- Create: `src/RouteTimer.Services/Weather/TimelineRouteEnvironment.cs`
- Create: `src/RouteTimer.Api/Weather/TrainingWeatherReconciler.cs`
- Modify: `src/RouteTimer.Services/Training/ParseTrainingJobHandler.cs`
- Modify: `src/RouteTimer.Services/Training/EnrichTrainingWeatherJobHandler.cs`
- Modify: `src/RouteTimer.Services/Models/BuildModelJobHandler.cs`
- Modify: `src/RouteTimer.Services/Models/IModelValidator.cs`
- Modify: `src/RouteTimer.Services/Models/ModelValidator.cs`
- Modify: `src/RouteTimer.Services/Models/ModelRebuildService.cs`
- Modify: `src/RouteTimer.Services/Jobs/JobProgressReporter.cs`
- Modify: `src/RouteTimer.Domain/Models/RiderModelAggregateValidator.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `tests/RouteTimer.Services.Tests/Training/ParseTrainingJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Training/EnrichTrainingWeatherJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/BuildModelJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/ModelValidatorTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/ModelRebuildServiceTests.cs`
- Create: `tests/RouteTimer.Api.Tests/Weather/TrainingWeatherReconcilerTests.cs`

**Interfaces:**

```csharp
public sealed record WeatherActivityEvidence(
    Guid ActivityId,
    CleanedActivity Activity,
    WeatherTimeline Timeline,
    WeatherResolvedActivity Resolved);

public interface IModelValidator
{
    ModelValidationSummary Validate(
        RiderProfile profile,
        IReadOnlyList<WeatherActivityEvidence> activities,
        double wetThresholdMillimetres,
        double strongCrosswindMetresPerSecond,
        double wetDescentMultiplier);
}
```

Set `BuildModelJobHandler.AlgorithmVersion` and `RiderModelAggregateValidator.CurrentAlgorithmVersion` to the same stable literal `weather-v1`.

- [ ] **Step 1: Write failing upload-chain tests**

Change parse-handler assertions so `SaveAsync`'s returned activity ID becomes the subject of exactly one `EnrichTrainingWeather` job and no BuildModel job. Change enrichment-handler assertions so every terminal outcome (Ready, AlreadyReady, Unavailable, final Failed) coalesces a BuildModel successor; retryable non-final attempts do not.

- [ ] **Step 2: Implement the upload-chain switch**

Update handlers only after tests fail. Preserve parse progress and permanent FIT error behavior. Queue the build before throwing a final permanent weather-job failure so the remaining Ready evidence can rebuild.

- [ ] **Step 3: Write failing build-gating tests**

Assert:

- Pending otherwise-eligible evidence exits without calling builders or saving a model;
- Ready + Pending does not publish a partial model;
- Ready + Failed/Unavailable builds from Ready only;
- no Ready eligible evidence fails with stable `no-weather-ready-activities`;
- raw Ready activities go unchanged to `PowerModelBuilder`;
- resolved activities and configured thresholds go to calibration/descent;
- calibration completes before descent receives its coefficients;
- saved model version is `weather-v1`.

- [ ] **Step 4: Implement evidence mapping and build orchestration**

Use `GetModelEvidenceAsync`. Filter existing activity eligibility before weather counts. For every Ready item, construct `WeatherTimeline`, call `WeatherActivityResolver.Resolve`, and form `WeatherActivityEvidence`. Treat invalid persisted weather as a permanent `ModelBuildException("invalid-training-weather", ...)`, not a transient worker retry.

Build in this order: power from `.Activity`; calibration from `.Resolved`; descent from `.Resolved` plus calibration coefficients; validation from full weather evidence; save. Report a new `waiting-for-training-weather` progress stage on the gated return.

- [ ] **Step 5: Write failing weather-aware validation tests**

Use three or more activities with differing winds. Assert each fold excludes the held-out activity by position/ID, trains fold power/calibration/descent from remaining evidence, constructs a route environment from the held-out timeline with start `heldOut.Activity.Metadata.StartedAt`, and calls the predictor with that environment and configured wet multiplier. Assert incomplete folds are skipped and existing percentile/status rules remain.

- [ ] **Step 6: Implement validation**

For each fold, build components in the same order as the real model. Process held-out positions, then use `TimelineRouteEnvironment`, whose `Resolve` method delegates with segment cumulative distance and the absolute time supplied by the predictor. Pass `PredictionEnvironment` with the held-out start and configured wet threshold/multiplier; Task 08 injects `WeatherOptionsValues` into `ModelValidator`.

- [ ] **Step 7: Write failing reconciler tests**

Assert it waits until `MigrationState.IsReady`, obtains IDs in configured bounded batches, calls `EnqueueIfNotPendingAsync(EnrichTrainingWeather,id)`, attempts each ID at most once per process start, does not block application readiness, propagates host cancellation, and makes no weather HTTP call itself. Include Pending, Failed, stale Ready, current Ready, and Unavailable repository fixtures.

- [ ] **Step 8: Implement and register `TrainingWeatherReconciler`**

Use `BackgroundService`, `IServiceScopeFactory`, `MigrationState`, options, and a process-local `HashSet<Guid>`. Yield immediately, wait in cancellable one-second intervals for migrations, then request batches and enqueue IDs not yet seen until no unseen IDs remain. Exit; do not loop forever and immediately retry Failed rows in the same process. A restart creates a fresh set and retries Failed rows.

- [ ] **Step 9: Update manual rebuild semantics**

`ModelRebuildService` still requires profile and eligible rides. It may enqueue while Pending; the build job then reports the waiting stage and preserves the current model. Do not make the HTTP request wait for weather.

- [ ] **Step 10: Run focused workflow tests**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~ParseTrainingJobHandlerTests|FullyQualifiedName~EnrichTrainingWeatherJobHandlerTests|FullyQualifiedName~BuildModelJobHandlerTests|FullyQualifiedName~ModelValidatorTests|FullyQualifiedName~ModelRebuildServiceTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter FullyQualifiedName~TrainingWeatherReconcilerTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: all pass.

- [ ] **Step 11: Commit and push**

```bash
git add src/RouteTimer.Domain/Models src/RouteTimer.Services src/RouteTimer.Api/Weather src/RouteTimer.Api/Program.cs tests
git commit -m "feat: rebuild rider models from weather-ready rides"
git push
git status --short
```

Expected: successful push and empty status.

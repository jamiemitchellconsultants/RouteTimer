# Pacing Strategy Adjustments Implementation Plan

> **For Codex:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task by task, preserving the review checkpoints and TDD order below.

**Goal:** Add five independently selectable pacing-adjustment strategies beneath an immutable, primary prediction baseline, while retaining multiple adjustment runs and allowing the rider to return to the baseline at any time.

**Architecture:** Keep `Prediction` as the existing aggregate and public baseline. Add append-only `PredictionAdjustment` children with their own durable job lifecycle, result/report JSON, and adjusted segment rows. Refactor the predictor around a persisted-segment-compatible `PredictionRoute`, inject a segment-aware power policy, and make every strategy rerun the complete sequential physics simulation. Expose nested adjustment APIs and render one selected adjustment beside the always-primary baseline.

**Tech Stack:** .NET 10, C# 14, ASP.NET Core Minimal APIs, EF Core with PostgreSQL, Blazor WebAssembly, System.Text.Json polymorphism, xUnit, bUnit, Testcontainers PostgreSQL, and the existing JavaScript route chart.

**Approved design:** `docs/superpowers/specs/2026-08-27-pacing-strategy-adjustments-design.md`

---

## Non-negotiable constraints

- `POST /api/predictions` remains baseline-only and its existing request and response stay compatible.
- A prediction baseline is immutable and remains the primary detail, history, GPX, and Garmin result.
- A succeeded baseline may retain multiple sibling adjustments. Creating one never replaces another.
- Each adjustment contains exactly one strategy; composition is out of scope.
- Existing succeeded predictions are eligible, using their captured model, profile, assumptions, and persisted segments.
- Adjusted GPX and Garmin course export are out of scope.
- Strategy JSON is limited to 64 KiB; rule and phase lists are limited to ten entries.
- Parent and per-strategy feature flags default to `false`.
- Adjustment warnings come from a closed catalog separate from baseline prediction warnings.
- The UI describes model outputs and feasibility, not medical, physiological, or coaching advice.

## Target file map

The exact names below keep adjustment code together and avoid turning the baseline repository and endpoint file into strategy dispatchers.

```text
src/RouteTimer.Domain/
  Predictions/PredictionRoute.cs
  Predictions/PowerTargetContext.cs
  Adjustments/AdjustmentState.cs
  Adjustments/AdjustmentWarningCodes.cs
  Adjustments/PacingStrategyDefinition.cs
  Adjustments/PacingStrategyReport.cs
  Adjustments/PredictionAdjustmentAnnotation.cs

src/RouteTimer.Services/
  Predictions/IPowerTargetPolicy.cs
  Adjustments/IPacingStrategyHandler.cs
  Adjustments/PacingStrategyDispatcher.cs
  Adjustments/PacingStrategyJson.cs
  Adjustments/PredictionAdjustmentService.cs
  Adjustments/PredictionAdjustmentQueryService.cs
  Adjustments/PredictionAdjustmentDeletionService.cs
  Adjustments/PredictionAdjustmentJobHandler.cs
  Adjustments/BoundedPacingSearch.cs
  Adjustments/NormalizedPowerCalculator.cs
  Adjustments/SegmentGains/
  Adjustments/NpIf/
  Adjustments/TimeTarget/
  Adjustments/Zones/
  Adjustments/MatchBurning/
  Persistence/IPredictionAdjustmentRepository.cs

src/RouteTimer.Persistence/
  Entities/PredictionAdjustmentEntity.cs
  Entities/PredictionAdjustmentSegmentEntity.cs
  Repositories/PredictionAdjustmentRepository.cs
  Migrations/*_AddPredictionAdjustments.*

src/RouteTimer.Contracts/
  Adjustments/PacingStrategyContracts.cs
  Adjustments/PredictionAdjustmentContracts.cs

src/RouteTimer.Api/
  Adjustments/PacingStrategyOptions.cs
  Endpoints/PredictionAdjustmentEndpoints.cs

src/RouteTimer.Client/
  Components/Adjustments/AdjustmentBuilder.razor
  Components/Adjustments/AdjustmentList.razor
  Components/Adjustments/AdjustmentComparison.razor
  Components/Adjustments/*StrategyEditor.razor
  Components/PredictionVisualization.razor
  Pages/PredictionDetail.razor
  Api/IRouteTimerApiClient.cs
  Api/RouteTimerApiClient.cs
```

Each production file gets a corresponding test in the existing project for its layer. Prefer focused files under `tests/*/Adjustments/` over extending already-large prediction test classes, except where an existing test is specifically protecting baseline behavior.

### Task 1: Introduce `PredictionRoute` without changing baseline output

**Files:**

- Create: `src/RouteTimer.Domain/Predictions/PredictionRoute.cs`
- Modify: `src/RouteTimer.Services/Predictions/IRoutePredictor.cs`
- Modify: `src/RouteTimer.Services/Predictions/RoutePredictor.cs`
- Modify: `src/RouteTimer.Services/Predictions/PredictionJobHandler.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionFixtures.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/RoutePredictorTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionWorkflowTests.cs`

**Step 1: Add a failing baseline-parity test**

Before changing the predictor signature, capture the current mixed-route result as an explicit golden `PredictionResult` fixture (including every segment and warning). Map the same input samples with `Skip(1)` into the proposed route and compare every public field and warning in order:

```csharp
[Fact]
public void PredictionRoute_refactor_preserves_the_complete_baseline_result()
{
    var processed = PredictionFixtures.MixedProcessedRoute();
    var expected = PredictionFixtures.MixedRouteGoldenResult();

    var actual = PredictionFixtures.Predict(PredictionRoute.FromProcessed(processed));

    Assert.Equal(expected, actual);
}
```

This must fail to compile before the route type exists.

**Step 2: Run the focused tests and record the expected failure**

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~RoutePredictor -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: compile failure naming `PredictionRoute`.

**Step 3: Add the simulation-only route records**

Implement immutable records with constructor validation for a non-empty, contiguous sequence and finite non-negative route values:

```csharp
public sealed record PredictionRoute(
    IReadOnlyList<PredictionRouteSegment> Segments,
    double DistanceMetres,
    double AscentMetres);

public sealed record PredictionRouteSegment(
    int Sequence,
    double Latitude,
    double Longitude,
    double ElevationMetres,
    double CumulativeDistanceMetres,
    double SegmentDistanceMetres,
    double Gradient,
    double CurvaturePerMetre);
```

Put mapping functions at orchestration boundaries: `PredictionJobHandler` maps `ProcessedRoute.Samples.Skip(1)`, while later the adjustment job maps persisted baseline segments. Do not make the domain type depend on parser-specific `ProcessedRoute`.

**Step 4: Change the predictor signature and remove `Skip(1)` from its loop**

```csharp
PredictionResult Predict(
    PredictionRoute route,
    RiderProfile profile,
    RiderModel model,
    CancellationToken cancellationToken = default);
```

The loop consumes `route.Segments` directly. Preserve validation order, warning order, entry-speed state, duration-band lookup, confidence aggregation, and all existing error translation.

**Step 5: Run all service tests**

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: all service tests pass, including the parity test.

**Step 6: Commit**

```bash
git add src/RouteTimer.Domain/Predictions/PredictionRoute.cs src/RouteTimer.Services/Predictions tests/RouteTimer.Services.Tests/Predictions
git commit -m "refactor: make prediction routes replayable"
```

### Task 2: Add the segment-aware power-policy seam

**Files:**

- Create: `src/RouteTimer.Domain/Predictions/PowerTargetContext.cs`
- Create: `src/RouteTimer.Services/Predictions/IPowerTargetPolicy.cs`
- Modify: `src/RouteTimer.Services/Predictions/IRoutePredictor.cs`
- Modify: `src/RouteTimer.Services/Predictions/RoutePredictor.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/RoutePredictorTests.cs`

**Step 1: Add failing policy tests**

Add tests proving:

1. a `null` policy is bit-for-bit identical to the baseline;
2. the policy sees the full segment, elapsed moving time before that segment, and the untouched model estimate;
3. the resolved power changes duration through the real physics loop; and
4. negative, NaN, or infinite policy output becomes `PredictionCalculationException`.

Use a recording policy rather than a mocked predictor:

```csharp
private sealed class RecordingPolicy(Func<PowerTargetContext, PowerEstimate> resolve)
    : IPowerTargetPolicy
{
    public List<PowerTargetContext> Contexts { get; } = [];

    public PowerEstimate Resolve(PowerTargetContext context)
    {
        Contexts.Add(context);
        return resolve(context);
    }
}
```

**Step 2: Verify the tests fail for the missing seam**

Run the same focused `RoutePredictor` test command from Task 1. Expected: compile failure for `IPowerTargetPolicy`.

**Step 3: Implement the policy seam**

Add `IPowerTargetPolicy? powerTargetPolicy = null` before the cancellation token. For every segment:

```csharp
var baseline = powerLookup.Estimate(segment.Gradient, elapsedMovingTime);
var estimate = powerTargetPolicy?.Resolve(
    new PowerTargetContext(segment, elapsedMovingTime, baseline)) ?? baseline;
ValidatePowerEstimate(estimate);
```

Keep `PowerLookup` construction inside the predictor so all policies modify the captured model estimate instead of substituting a model.

**Step 4: Run service tests and commit**

Run all service tests, then:

```bash
git add src/RouteTimer.Domain/Predictions src/RouteTimer.Services/Predictions tests/RouteTimer.Services.Tests/Predictions
git commit -m "feat: add prediction power target policies"
```

### Task 3: Define adjustment domain types, contracts, validation, and canonical JSON

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/AdjustmentState.cs`
- Create: `src/RouteTimer.Domain/Adjustments/AdjustmentWarningCodes.cs`
- Create: `src/RouteTimer.Domain/Adjustments/PacingStrategyDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/PacingStrategyReport.cs`
- Create: `src/RouteTimer.Domain/Adjustments/PredictionAdjustmentAnnotation.cs`
- Create: `src/RouteTimer.Contracts/Adjustments/PacingStrategyContracts.cs`
- Create: `src/RouteTimer.Contracts/Adjustments/PredictionAdjustmentContracts.cs`
- Create: `src/RouteTimer.Services/Adjustments/PacingStrategyJson.cs`
- Create: `src/RouteTimer.Services/Adjustments/PacingStrategyValidationException.cs`
- Modify: `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`
- Test: `tests/RouteTimer.Domain.Tests/Adjustments/PacingStrategyDefinitionTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/PacingStrategyJsonTests.cs`

**Step 1: Write failing closed-union tests**

Test all five stable discriminators, unknown-discriminator rejection, subtype/discriminator mismatch, duplicate rule IDs, non-finite values, reversed ranges, list limits, and the 64 KiB serialized limit. Also assert `AdjustmentWarningCodes.IsKnown` rejects baseline and arbitrary warning strings.

**Step 2: Add the domain union**

Use these stable values:

```csharp
public enum PacingStrategyType
{
    SegmentSpecificGains,
    NpIfTarget,
    TimeTarget,
    RpeZoneShift,
    VariableMatchBurning
}

public abstract record PacingStrategyDefinition(PacingStrategyType Type);
public abstract record PacingStrategyReport(PacingStrategyType Type);
```

Add the exact strategy records approved in the design. Keep `Definition` and `Report` immutable. Store per-segment optional values in `PredictionAdjustmentAnnotation(int? ZoneNumber, string? StrategyPhase, double? WPrimeBalanceJoules)`.

**Step 3: Add polymorphic HTTP contracts**

Annotate only the contract request and response roots with `JsonPolymorphic` and one `JsonDerivedType` per stable discriminator. The API mapper must exhaustively translate each contract subtype to a domain subtype. Do not add a strategy property to baseline submission contracts.

**Step 4: Canonicalize in services**

Configure a dedicated `JsonSerializerOptions` with deterministic camel-case property names, explicit enum strings, no indentation, and rejection of named floating-point literals. Round-trip through the expected domain subtype before persisting. Validate UTF-8 byte count after canonicalization.

**Step 5: Add public error codes**

Add stable codes for adjustment not found, baseline not ready, strategy disabled, invalid strategy, capacity inputs required, and target infeasible. Map detailed field errors later at the API boundary; never persist arbitrary validation messages as warning codes.

**Step 6: Run domain and service tests, then commit**

```bash
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~Adjustments -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Domain/Adjustments src/RouteTimer.Contracts/Adjustments src/RouteTimer.Services/Adjustments src/RouteTimer.Contracts/Errors tests/RouteTimer.Domain.Tests/Adjustments tests/RouteTimer.Services.Tests/Adjustments
git commit -m "feat: define pacing adjustment contracts"
```

### Task 4: Persist adjustment aggregates and enforce ownership

**Files:**

- Create: `src/RouteTimer.Persistence/Entities/PredictionAdjustmentEntity.cs`
- Create: `src/RouteTimer.Persistence/Entities/PredictionAdjustmentSegmentEntity.cs`
- Create: `src/RouteTimer.Services/Persistence/IPredictionAdjustmentRepository.cs`
- Create: `src/RouteTimer.Persistence/Repositories/PredictionAdjustmentRepository.cs`
- Modify: `src/RouteTimer.Persistence/Entities/PredictionEntity.cs`
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Create: `src/RouteTimer.Persistence/Migrations/*_AddPredictionAdjustments.cs`
- Create: `src/RouteTimer.Persistence/Migrations/*_AddPredictionAdjustments.Designer.cs`
- Modify: `src/RouteTimer.Persistence/Migrations/RouteTimerDbContextModelSnapshot.cs`
- Test: `tests/RouteTimer.Persistence.Tests/PredictionAdjustmentRepositoryTests.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/PostgresMigrationTests.cs`

**Step 1: Write failing repository tests**

Cover:

- create under a succeeded baseline and reject queued/running/failed/cancelled/missing baselines;
- list newest-first and fetch only when both baseline and adjustment IDs match;
- preserve canonical strategy JSON exactly;
- publish summary, report, warnings, annotations, and all segment values atomically;
- reject unknown warnings and sequence sets differing from the baseline;
- reject stale job/worker publication;
- delete one child without touching the baseline or sibling;
- cascade adjustment rows when the baseline is deleted; and
- round-trip through PostgreSQL, not only EF InMemory.

**Step 2: Map the schema**

Add `prediction_adjustments` and `prediction_adjustment_segments` exactly as specified. Use:

- FK `PredictionId -> predictions.Id ON DELETE CASCADE`;
- composite PK `(AdjustmentId, Sequence)` for child segments;
- unique index `(PredictionId, Id)` if needed for composite ownership joins;
- index `(PredictionId, CreatedAt DESC)`;
- max lengths for state, strategy type, version, confidence, and phase;
- `jsonb` for strategy, report, and warnings; and
- finite/range validation in repository publication before EF mutation.

Do not copy baseline geometry into adjusted rows. Query details by joining each adjusted sequence to the owning baseline segment.

**Step 3: Generate the migration with the repository’s normal EF command**

```bash
dotnet ef migrations add AddPredictionAdjustments --project src/RouteTimer.Persistence --startup-project src/RouteTimer.Api
```

Inspect generated SQL semantics and ensure both cascades and indexes are present. Do not hand-edit the model snapshot.

**Step 4: Run persistence tests**

```bash
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: repository, PostgreSQL migration, and `Model_has_no_pending_changes` tests pass.

**Step 5: Commit**

```bash
git add src/RouteTimer.Persistence src/RouteTimer.Services/Persistence tests/RouteTimer.Persistence.Tests
git commit -m "feat: persist prediction adjustments"
```

### Task 5: Add durable adjustment creation, query, deletion, and job orchestration

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

### Task 6: Expose nested APIs and capabilities

**Files:**

- Create: `src/RouteTimer.Api/Adjustments/PacingStrategyOptions.cs`
- Create: `src/RouteTimer.Api/Endpoints/PredictionAdjustmentEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Api/appsettings.json`
- Modify: `src/RouteTimer.Api/appsettings.Development.json`
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`
- Modify: `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`
- Test: `tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/Api/RouteTimerApiClientTests.cs`

**Step 1: Add failing endpoint contract tests**

Test all five routes:

```text
GET    /api/pacing-strategies
POST   /api/predictions/{predictionId}/adjustments
GET    /api/predictions/{predictionId}/adjustments
GET    /api/predictions/{predictionId}/adjustments/{adjustmentId}
DELETE /api/predictions/{predictionId}/adjustments/{adjustmentId}
```

Assert nested ownership returns 404 rather than leaking that an adjustment belongs to another baseline. Assert POST returns 202 with `Location` pointing to the nested detail, and list ordering is newest-first. Assert parent/per-strategy disabled, malformed discriminator, >64 KiB payload, baseline not ready, and list-limit failures map to stable Problem Details codes.

**Step 2: Implement feature options and capability response**

Bind:

```json
{
  "PacingStrategies": {
    "Enabled": false,
    "SegmentSpecificGains": false,
    "NpIfTarget": false,
    "TimeTarget": false,
    "RpeZoneShift": false,
    "VariableMatchBurning": false,
    "MaximumDefinitionBytes": 65536,
    "MaximumRules": 10,
    "MaximumPhases": 10
  }
}
```

The capability response is the only source the client uses to decide which editors to show. Configuration is an availability gate, not a substitute for server validation.

**Step 3: Implement endpoint mapping**

Keep the new mapper separate from `PredictionEndpoints`. Catch only known service exceptions and map them through `ApiProblems`; allow cancellation and unknown failures to follow existing middleware behavior.

**Step 4: Add client methods**

Add typed list/detail/create/delete/capability methods and fake-client recording collections. Do not add adjustment fields to `PredictionDetailResponse`; the page loads the child collection separately.

**Step 5: Run API and client API tests, then commit**

```bash
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter FullyQualifiedName~PredictionAdjustmentEndpoint -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~RouteTimerApiClient -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Api src/RouteTimer.Client/Api tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs tests/RouteTimer.Client.Tests/Api tests/RouteTimer.Client.Tests/Fakes
git commit -m "feat: expose prediction adjustment APIs"
```

### Task 7: Build the baseline-primary adjustment shell in the client

**Files:**

- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentBuilder.razor`
- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentList.razor`
- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentComparison.razor`
- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentSummaryCard.razor`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor.css`
- Modify: `src/RouteTimer.Client/Jobs/JobPoller.cs`
- Test: `tests/RouteTimer.Client.Tests/PredictionAdjustmentShellTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionDetailPageTests.cs`

**Step 1: Add failing bUnit tests for primary/secondary behavior**

Prove that:

- baseline summary and baseline visualization render first and never disappear;
- controls are absent for incomplete baselines or a disabled parent capability;
- multiple adjustments remain listed after another is created;
- only one adjustment can be selected for comparison;
- “Back to baseline” clears selection without deleting anything;
- queued/running children poll independently and terminal children stop polling;
- failed/cancelled children retain their row and readable state; and
- deleting a selected child returns to baseline and leaves siblings.

**Step 2: Implement state ownership in `PredictionDetail`**

The page owns `baseline`, `capabilities`, `adjustmentSummaries`, and `selectedAdjustmentId`. Child components receive immutable parameters and callbacks. Baseline load failure remains governed by existing behavior; adjustment-list failure shows an inline secondary error and must not hide the baseline.

**Step 3: Implement comparison semantics**

The comparison card labels columns “Baseline” and the strategy display name. Show deltas for moving time, average speed, and duration-weighted average power. Warnings and strategy reports belong only to the selected adjustment. Do not put adjusted GPX or Garmin actions in this card.

**Step 4: Run client tests and commit**

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionAdjustmentShell|FullyQualifiedName~PredictionDetailPage" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Client/Components/Adjustments src/RouteTimer.Client/Pages/PredictionDetail.razor src/RouteTimer.Client/Pages/PredictionDetail.razor.css src/RouteTimer.Client/Jobs tests/RouteTimer.Client.Tests
git commit -m "feat: add baseline adjustment comparison shell"
```

### Task 8: Deliver segment-specific gains end to end

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/SegmentGains/SegmentGainsDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/SegmentGains/SegmentGainsReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/SegmentGains/SegmentGainsPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/SegmentGains/SegmentGainsHandler.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/SegmentGainsEditor.razor`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/SegmentGainsHandlerTests.cs`
- Test: `tests/RouteTimer.Client.Tests/SegmentGainsEditorTests.cs`
- Modify contract, API mapping, DI, dispatcher, and report rendering files created in Tasks 3, 5, 6, and 7.

**Step 1: Write failing rule tests**

Cover each selector (`Distance`, `Gradient`, `Sequence`), inclusive boundaries, ordered first-match precedence, one-selector-only validation, exactly one of factor/delta, negative deltas, 10 W floor, unchanged unmatched segments, ten-rule limit, and rule hit counts.

**Step 2: Implement deterministic matching**

Canonicalize rules in submitted order. Precompute `sequence -> applied rule ID` from route geometry, then resolve:

```csharp
var watts = rule.Factor is { } factor
    ? context.BaselineEstimate.Watts * factor
    : context.BaselineEstimate.Watts + rule.DeltaWatts!.Value;
return context.BaselineEstimate with { Watts = Math.Max(10, watts) };
```

Preserve the baseline estimate’s evidence and confidence. The report contains matched/unmatched segment counts, per-rule hit count, and route-level deltas.

**Step 3: Add the editor and report**

Allow adding, ordering, and removing up to ten rules. Switching selector or adjustment mode clears fields belonging to the old choice before submit. Render server field errors next to the owning rule.

**Step 4: Run focused service/API/client tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~SegmentGains -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter FullyQualifiedName~SegmentSpecificGains -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~SegmentGains -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src tests
git commit -m "feat: add segment-specific pacing gains"
```

### Task 9: Implement bounded full-simulation search and normalized power primitives

**Files:**

- Create: `src/RouteTimer.Services/Adjustments/BoundedPacingSearch.cs`
- Create: `src/RouteTimer.Services/Adjustments/NormalizedPowerCalculator.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/BoundedPacingSearchTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/NormalizedPowerCalculatorTests.cs`

**Step 1: Write failing search tests**

Use a fake evaluator to prove fixed coarse-grid evaluation, adjacent sign-change bracket selection, bisection, exact-bound hits, closest-valid fallback without a bracket, invalid-candidate diagnostics, deterministic tie-breaking, cancellation between evaluations, and a hard evaluation cap.

**Step 2: Implement a strategy-neutral search result**

```csharp
public sealed record PacingSearchCandidate<T>(
    double Parameter,
    T? Value,
    double? Objective,
    string? FailureCode);

public sealed record PacingSearchResult<T>(
    PacingSearchCandidate<T> Selected,
    IReadOnlyList<PacingSearchCandidate<T>> Evaluated,
    bool Converged,
    bool Bracketed);
```

Require finite ordered bounds, fixed grid size, tolerance, and max evaluations. Never retry a parameter already evaluated.

**Step 3: Implement exact one-second NP resampling**

Expand piecewise-constant segment power onto one-second buckets, weighting partial first/last seconds. Compute a trailing 30-second rolling mean for each full window, raise each mean to the fourth power, average, then take the fourth root. Routes under 30 seconds return duration-weighted mean power plus the `np-if-short-route-approximation` warning rather than NaN.

**Step 4: Test duration boundaries**

Include 29.999 s, 30 s, 30.001 s, a constant-power route where NP equals power, unequal segment durations, fractional final seconds, and non-finite input rejection.

**Step 5: Run tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~BoundedPacingSearch|FullyQualifiedName~NormalizedPowerCalculator" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Services/Adjustments tests/RouteTimer.Services.Tests/Adjustments
git commit -m "feat: add bounded pacing search primitives"
```

### Task 10: Deliver time-target pacing end to end

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/TimeTarget/TimeTargetDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/TimeTarget/TimeTargetReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/TimeTarget/TimeTargetPowerPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/TimeTarget/TimeTargetHandler.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/TimeTargetEditor.razor`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/TimeTargetHandlerTests.cs`
- Test: `tests/RouteTimer.Client.Tests/TimeTargetEditorTests.cs`
- Modify shared contract, mapping, DI, and report files.

**Step 1: Write failing distribution and feasibility tests**

Test proportional mode, climb-focused exact normalization, zero climb-weight fallback, no `EvenEffort` discriminator, faster and slower targets, targets equal to baseline, impossible targets, duration tolerance, hard evaluation cap, and closest-feasible reporting.

**Step 2: Implement exact climb-focused normalization**

Classify climbs as gradient at least 3%. Compute their fraction `f` of baseline moving time. For outer scale `S` and climb bias `b`, precompute `climbScale = S * b / (f * b + 1 - f)` and `otherScale = S / (f * b + 1 - f)`. This keeps the baseline-time-weighted mean factor exactly `S`. A route with no qualifying climb falls back to proportional and adds `time-target-no-climbs`.

**Step 3: Search complete simulations**

Use `adjusted.MovingTime.TotalSeconds - targetSeconds` as objective. Report the feasible interval obtained from bound candidates, requested/achieved time, absolute/percentage miss, distribution, scalar, convergence, evaluation count, gradient-band demand ratios, and the approved feasibility verdict. Reject nonsensical times before enqueue; publish closest valid with a warning when physically infeasible.

**Step 4: Add duration input and report UI**

Use an accessible `hh:mm:ss` editor with explicit parsing errors. Show faster/slower delta relative to baseline and label feasibility as a model result.

**Step 5: Run focused tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~TimeTarget -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~TimeTarget -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src tests
git commit -m "feat: add time target pacing"
```

### Task 11: Deliver NP/IF targeting end to end

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/NpIf/NpIfTargetDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/NpIf/NpIfTargetReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/NpIf/NpIfPowerPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/NpIf/NpIfTargetHandler.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/NpIfTargetEditor.razor`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/NpIfTargetHandlerTests.cs`
- Test: `tests/RouteTimer.Client.Tests/NpIfTargetEditorTests.cs`
- Modify shared contract, mapping, DI, and report files.

**Step 1: Write failing strategy tests**

Cover explicit FTP validation, target IF bounds, proportional and additive modes, under-30-second mean-power fallback, under-ten-minute approximation warning, exact target, unreachable high/low target, no-bracket closest result, candidate failure recovery, evaluation cap, and cancellation.

**Step 2: Implement the objective**

Each candidate creates either a proportional or additive policy, calls the real `IRoutePredictor`, computes NP from the resulting segment durations, and evaluates:

```csharp
objective = normalizedPowerWatts - ftpWatts * targetIntensityFactor;
```

Use fixed bounds and tolerances from the approved design. The report stores requested FTP/IF, achieved NP/IF, mode, selected parameter, convergence, evaluation count, and route-level deltas. Add a known warning for closest-feasible fallback.

**Step 3: Add editor and report rendering**

Explain that FTP is an input to this adjustment and does not modify the rider model. Disable submit until the client’s basic field validation passes; server validation remains authoritative.

**Step 4: Run focused tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~NpIf -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~NpIf -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src tests
git commit -m "feat: add normalized power targeting"
```

### Task 12: Deliver FTP and inferred zone targeting end to end

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/Zones/ZoneShiftDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/Zones/ZoneShiftReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/Zones/PowerZoneResolver.cs`
- Create: `src/RouteTimer.Services/Adjustments/Zones/ZoneShiftPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/Zones/ZoneShiftHandler.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/ZoneShiftEditor.razor`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/PowerZoneResolverTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/ZoneShiftHandlerTests.cs`
- Test: `tests/RouteTimer.Client.Tests/ZoneShiftEditorTests.cs`
- Modify shared contract, mapping, DI, and report files.

**Step 1: Write failing zone-boundary tests**

Cover all seven FTP zone boundaries, finite upper target for zone 7, all five inferred zones, threshold inference `GlobalTypicalWatts / 0.83`, supplied-versus-inferred mode validation, ordered gradient assignments before the all-segments fallback, unmatched segments remaining unchanged, selected lower/midpoint/upper targets within the resolved zone, and duration-weighted zone distribution totaling 100% within rounding tolerance.

**Step 2: Implement one authoritative resolver**

Return both absolute watt boundaries and provenance (`SuppliedFtp` or `InferredModel`). Avoid duplicating percentages in the client. Persist the resolved threshold and boundaries in the report so historical adjustments remain explainable if constants change.

**Step 3: Implement policy and report**

Evaluate ordered gradient assignments before the optional all-segments assignment. For the first match, choose the requested finite lower-bound, midpoint, or upper-bound target in the requested zone; unmatched segments retain the baseline estimate. Preserve confidence/evidence. Annotate each adjusted segment with the resulting zone number and add `rpe-zone-z7-capped` when the finite Zone 7 ceiling is selected.

**Step 4: Add editor and distribution report**

Let the rider choose FTP-based or model-inferred zones and manage up to ten ordered assignments with gradient/all-segments selectors, zone, and placement. Show the provenance disclaimer and render duration and percentage by zone.

**Step 5: Run focused tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~ZoneShift|FullyQualifiedName~PowerZoneResolver" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~ZoneShift -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src tests
git commit -m "feat: add power zone pacing adjustments"
```

### Task 13: Deliver variable match-burning end to end

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/MatchBurning/MatchBurningDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/MatchBurning/MatchBurningReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/CapacityResolver.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/MatchPhasePlanner.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/WPrimeBalanceCalculator.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/MatchBurningPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/MatchBurningHandler.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/MatchBurningEditor.razor`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/CapacityResolverTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/MatchPhasePlannerTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/WPrimeBalanceCalculatorTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/MatchBurningHandlerTests.cs`
- Test: `tests/RouteTimer.Client.Tests/MatchBurningEditorTests.cs`
- Modify shared contract, mapping, DI, and report files.

**Step 1: Write failing capacity and phase tests**

Cover supplied CP/W-prime validation, model inference and its provenance, unavailable inference, phase distance boundaries, ordered first-match priority, ten-phase limit, hold/recovery/match targets, segment annotations, and deterministic optional one-pass refinement.

**Step 2: Implement exponential W-prime balance tests first**

Test depletion above CP:

```text
Wbal_next = max(0, Wbal - (P - CP) * dt)
```

and reconstitution below CP:

```text
DCP = CP - P
tau = 546 * exp(-0.01 * DCP) + 316
Wbal_next = Wprime - (Wprime - Wbal) * exp(-dt / tau)
```

Include exact CP, zero duration, large duration, floor/ceiling behavior, and non-finite rejection. Do not reintroduce the earlier fixed linear recovery approximation.

**Step 3: Implement handler flow**

Resolve capacity, precompute phase by sequence, run the full prediction once, calculate W-prime trajectory from actual segment durations, and optionally run one deterministic refinement when the configured reserve constraint is missed. Never loop refinement without a fixed cap of one.

**Step 4: Persist report and annotations**

Report supplied/inferred CP and W-prime with provenance, minimum/final W-prime balance, time above CP, work above CP, reserve breaches, phase summaries, whether refinement ran, and route-level deltas. Annotate each segment with phase and W-prime balance. Add known warnings for inferred capacity and reserve breach.

**Step 5: Add editor and disclaimer**

Allow supplied or inferred capacity and up to ten ordered phases. Explain that inferred values are model estimates for scenario comparison, not physiological measurements or training advice.

**Step 6: Run focused tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~MatchBurning|FullyQualifiedName~WPrime|FullyQualifiedName~CapacityResolver" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~MatchBurning -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src tests
git commit -m "feat: add variable match burning adjustments"
```

### Task 14: Add one-adjustment visualization overlays

**Files:**

- Modify: `src/RouteTimer.Client/Components/PredictionVisualization.razor`
- Modify: `src/RouteTimer.Client/Components/PredictionVisualization.razor.css`
- Modify: `src/RouteTimer.Client/wwwroot/js/prediction-visualization.js`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Modify: `tests/RouteTimer.Client.Tests/PredictionVisualizationTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionAdjustmentShellTests.cs`

**Step 1: Add failing rendering and interop tests**

Assert baseline-only rendering is byte-for-byte compatible at the component parameter boundary. With one selected adjustment, assert the interop payload contains aligned baseline/adjusted series by sequence, but never two adjusted series. Test missing/mismatched sequence rejection before JavaScript invocation.

**Step 2: Extend the chart payload**

Keep the baseline line visually dominant and stable. Draw the selected adjusted line with a distinct secondary style and legend label. Tooltips show baseline, adjusted, and delta for power/speed/time plus zone/phase/W-prime only when present.

**Step 3: Protect large-route behavior**

Reuse the existing downsampling path, preserving first/last points and strategy-boundary points. Do not perform an N-by-M sequence join in JavaScript; align once in C# and pass arrays.

**Step 4: Run client tests and commit**

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionVisualization|FullyQualifiedName~PredictionAdjustmentShell" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: visualize baseline adjustment comparisons"
```

### Task 15: Harden lifecycle, limits, and backward compatibility

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

### Task 16: Add rollout evidence and complete system verification

**Files:**

- Create: `docs/pacing-strategies/backtesting.md`
- Modify: `docs/pacing-strategies/06-cross-cutting-rollout.md`
- Modify: `README.md` only if it currently documents feature flags or operator configuration.
- Modify: deployment configuration examples that already carry application feature flags; do not invent a second configuration system.

**Step 1: Add a deterministic back-testing harness or fixture set**

Use representative retained route/model fixtures for flat, rolling, mountainous, short, and long routes. Record baseline invariance and, for each enabled strategy, finite output, sequence parity, deterministic reruns, evaluation count, and expected direction of time/power changes. Keep physiological interpretation out of pass/fail criteria.

**Step 2: Document staged enablement**

Document this order and rollback:

1. deploy schema and predictor refactor with all flags off;
2. enable parent plus segment gains for internal users;
3. enable time target and NP/IF after search telemetry is acceptable;
4. enable zones after provenance/report review;
5. enable match-burning last;
6. disable an individual strategy to stop new submissions while retaining access to historical children; and
7. disable the parent to hide creation while baseline prediction remains unaffected.

Include operational signals: adjustment queue age, runtime, evaluations per job, cancellation/failure counts by stable diagnostic code, and publication conflicts. Do not log full strategy JSON.

**Step 3: Run narrative verification**

Use the repository-configured Narrative compiler to check the correction fragment and generated `Narrative.md`:

```bash
node /private/tmp/RouteTimer-Narrative-tool/bin/narrative.mjs check --config .project-narrative.json
```

Expected: generated narrative is current and the correction cites `docs-add-pacing-strategy-implementation-plans`.

**Step 4: Run the complete solution**

```bash
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: every Domain, Services, Persistence, API, Client, and EndToEnd test passes. Record project-by-project counts in the PR.

**Step 5: Inspect the final diff**

```bash
git status --short
git diff --check
git diff --stat main...HEAD
```

Confirm:

- no baseline submission or response compatibility break;
- no adjustment export action;
- flags default off;
- generated migration and model snapshot agree;
- `Narrative.md` was generated, not hand-edited;
- no secrets or route/model payloads appear in logs; and
- all five strategies have service, API, persistence, and client coverage.

**Step 6: Commit**

```bash
git add docs README.md
git commit -m "docs: add pacing adjustment rollout evidence"
```

## Execution checkpoints

Pause for review after Tasks 2, 6, 8, 11, 13, and 16. Those checkpoints respectively validate baseline parity, the shared resource contract, the first complete vertical slice, the shared search family, the highest-risk strategy, and production readiness. At every checkpoint, use `superpowers:requesting-code-review`; before claiming completion, use `superpowers:verification-before-completion` with fresh command output.

Do not merge implementation as one unreviewed change. The preferred delivery is a sequence of pull requests that keep all new flags off until the required vertical slice is complete. If implementation is executed on one long-lived branch, retain the task commits above so each checkpoint has an auditable review range.

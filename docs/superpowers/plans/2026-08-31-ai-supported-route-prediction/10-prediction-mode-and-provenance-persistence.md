[← Plan overview](README.md)

# Prediction Mode and Provenance Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture Typical/Today intent and a compatible AI model at submission, then persist backward-compatible deterministic comparison and AI provenance fields.

**Architecture:** Prediction rows gain nullable additive AI columns. New submissions snapshot requested mode and current compatible model ID; publication receives one validated `PredictionAiPublication`. Legacy rows remain readable with null metadata.

**Tech Stack:** Domain/contracts, prediction services/repository, EF Core migration, API tests, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Existing pre-AI rows and APIs remain readable.
- Capture AI model ID at submission; never swap to a newer model in the background job.
- Typical is the omitted/default mode. Unknown modes return a stable 400 problem.
- Task 10 persists deterministic effective output; Task 11 replaces it with AI output only when serving permits.

### Task 10: Extend prediction schema, repository and contracts

**Files:**

- Create: `src/RouteTimer.Domain/Predictions/PredictionAiMetadata.cs`
- Modify: `src/RouteTimer.Domain/Predictions/PredictionWarningCodes.cs`
- Modify: `src/RouteTimer.Services/Persistence/IPredictionRepository.cs`
- Modify: `src/RouteTimer.Services/Predictions/PredictionSubmissionService.cs`
- Modify: `src/RouteTimer.Services/Predictions/PredictionJobHandler.cs`
- Modify: `src/RouteTimer.Persistence/Entities/PredictionEntity.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/PredictionRepository.cs`
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Create: `src/RouteTimer.Persistence/Migrations/*_AddPredictionAiProvenance.cs` with generated designer/snapshot changes
- Modify: `src/RouteTimer.Contracts/Predictions/PredictionContracts.cs`
- Modify: `src/RouteTimer.Api/Endpoints/PredictionsEndpoints.cs`
- Modify: `src/RouteTimer.Api/Errors/ApiProblems.cs`
- Modify: prediction service, repository, endpoint, contract-shape and migration tests

**Interfaces:**

```csharp
public sealed record PredictionAiPublication(
    PredictionMode RequestedMode,
    PredictionEffectiveMode EffectiveMode,
    TimeSpan DeterministicBaselineTime,
    Guid? AiModelId,
    double? EffortMultiplier,
    double? RouteMatchPercentage,
    int? SupportingRideCount,
    double? ComparableMedianApe,
    double? ComparableP90Ape,
    IReadOnlyList<AiFeatureContribution> Contributions,
    string? FallbackReason);
```

Extend:

```csharp
QueuedPredictionCreation(..., PredictionMode RequestedMode, Guid? AiModelId, ...);
PredictionForProcessing(..., PredictionMode RequestedMode, Guid? AiModelId, DateTimeOffset CreatedAt);
PredictionPublication(..., PredictionAiPublication Ai, ...);
```

Prediction response additions are nullable for legacy rows: `requestedMode`, `effectiveMode`, `deterministicBaselineSeconds`, `aiModelId`, `aiEffortAdjustmentPercent`, `aiRouteMatchPercent`, `aiSupportingRideCount`, `aiComparableMedianError`, `aiComparableP90Error`, `aiContributions`, and `aiFallbackReason`.

- [ ] **Step 1: Write failing domain/publication validation tests**

Assert deterministic effective mode requires null multiplier/match/errors and no contributions; AI modes require model ID, multiplier in serving bounds, match `[0,100]`, at least five rides, finite error metrics, zero to three valid contributions, and no fallback reason; fallback reasons come from a closed catalog. `PredictionJobHandler.BuildPublication` separately validates deterministic baseline equals final time. Assert Today may effectively become AiTypical or Deterministic, while requested Typical may not become AiToday.

- [ ] **Step 2: Implement metadata and warning/fallback catalogs**

Keep AI fallback reasons separate from predictor warning codes but validate both before persistence. Include `ai-model-unavailable`, `ai-model-incompatible`, `ai-serving-disabled`, `ai-shadow-mode`, `ai-comparison-mode`, route reasons from Task 06, Today reasons, and `ai-evaluation-fallback`. Contribution direction is exactly `increased-effort` or `reduced-effort`; codes come from the two fixed feature schemas.

- [ ] **Step 3: Write failing repository/migration tests**

Assert create stores requested mode and nullable model ID, processing returns them, publication atomically stores every field, legacy fixture rows with null columns map to null contract metadata, unknown stored modes/reasons are rejected, AI model FK uses Restrict, prediction deletion does not delete AI model, and exact database constraints prevent partial AI metadata.

- [ ] **Step 4: Implement entity, mapping, repository and migration**

Use nullable strings for requested/effective mode so old rows remain distinguishable. Add nullable numeric columns, nullable `RiderAiModelId`, and `AiContributions` JSONB with a validated empty-list default for new deterministic rows. Add check constraints for percentages/multiplier/count where non-null. Update all repository projections once; do not create a second AI-only query path.

Generate:

```bash
dotnet ef migrations add AddPredictionAiProvenance --project src/RouteTimer.Persistence --startup-project src/RouteTimer.Api
```

- [ ] **Step 5: Write failing submission/endpoint tests**

Assert omitted mode captures Typical, explicit case-insensitive `today` captures Today, unknown/multiple values produce `invalid-prediction-mode`, compatible published AI ID is captured, incompatible/missing/corrupt AI model becomes null without blocking deterministic submission, and the deterministic rider/profile snapshot is unchanged.

- [ ] **Step 6: Implement submission capture and API parsing**

Inject `IRiderAiModelRepository`. Call `GetCurrentCompatibleAsync(model.Model.AlgorithmVersion, profile, ct)` after loading the deterministic model. Extend multipart parsing with one optional small string field `mode`; preserve GPX size/streaming behaviour.

- [ ] **Step 7: Publish deterministic provenance until Task 11**

`PredictionJobHandler` still produces the current deterministic result. Build `PredictionAiPublication` with requested mode, effective Deterministic, baseline equal result time, captured model ID retained as submission provenance, empty contributions, and fallback `ai-serving-disabled` when a compatible model was captured or `ai-model-unavailable` otherwise. Task 11 replaces this branch with runner output.

- [ ] **Step 8: Run service, persistence, API and contract regressions**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~PredictionWorkflowTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionRepositoryTests|FullyQualifiedName~PostgresMigrationTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionEndpointTests|FullyQualifiedName~ProblemDetailsTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 9: Commit and push**

```bash
git add src/RouteTimer.Domain/Predictions src/RouteTimer.Services/Persistence src/RouteTimer.Services/Predictions src/RouteTimer.Persistence src/RouteTimer.Contracts/Predictions src/RouteTimer.Api tests
git commit -m "feat: capture AI prediction provenance"
git push
git status --short
```

Expected: successful push and empty status.

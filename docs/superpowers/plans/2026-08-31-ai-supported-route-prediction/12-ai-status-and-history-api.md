[← Plan overview](README.md)

# AI Status and History API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose global AI readiness, challenger/publication state, Today freshness, and safe manual history confirmation without changing deterministic model readiness.

**Architecture:** `AiStatusService` joins weather-ready evidence, current/latest AI evaluations, build-job state, and freshness into one nested contract. A separate authenticated endpoint advances manual confirmation using server time only.

**Tech Stack:** Services/contracts, ASP.NET Core minimal APIs, xUnit/API integration tests.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Existing top-level deterministic `ModelStatusResponse` fields keep their meaning.
- Status contains codes/data; the client owns human-readable copy.
- Manual confirmation has no request body or timestamp and is authenticated like Training endpoints.
- API errors reveal no model coefficients, feature vectors, route values, or activity names.

### Task 12: Add nested AI status and manual confirmation API

**Files:**

- Create: `src/RouteTimer.Services/Ai/Readiness/AiStatusService.cs`
- Create: `src/RouteTimer.Contracts/Models/AiModelContracts.cs`
- Modify: `src/RouteTimer.Contracts/Models/ModelContracts.cs`
- Modify: `src/RouteTimer.Services/Models/ModelStatusResult.cs`
- Modify: `src/RouteTimer.Services/Models/ModelStatusService.cs`
- Modify: `src/RouteTimer.Api/Endpoints/ModelsEndpoints.cs`
- Create: `src/RouteTimer.Api/Endpoints/TrainingHistoryEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Readiness/AiStatusServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/ModelStatusServiceTests.cs`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/ModelEndpointTests.cs`
- Create: `tests/RouteTimer.Api.Tests/Endpoints/TrainingHistoryEndpointTests.cs`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/ProblemDetailsTests.cs`

**Interfaces:**

```csharp
public sealed record AiReadinessContributorResponse(
    int Current, int Target, double Points, double MaximumPoints);

public sealed record AiModelStatusResponse(
    double ReadinessPercentage,
    string State,
    AiReadinessContributorResponse RideCount,
    AiReadinessContributorResponse DurationVariety,
    AiReadinessContributorResponse TerrainVariety,
    string? StrongestEvidenceCode,
    string? NextEvidenceCode,
    bool CanEvaluate,
    Guid? PublishedModelId,
    string? AlgorithmVersion,
    double? DeterministicMedianError,
    double? TypicalMedianError,
    double? TodayMedianError,
    string? LatestRejectionReason,
    bool TodayAvailable,
    bool HistoryCurrent,
    DateTimeOffset? HistoryConfirmedThrough,
    string? HistoryConfirmationSource,
    JobResponse? BuildJob);

public sealed record TrainingHistoryConfirmationResponse(
    DateTimeOffset ConfirmedThrough,
    string Source,
    bool IsCurrent);
```

Append nullable `AiModelStatusResponse? Ai` to `ModelStatusResponse` so older test fixtures and JSON remain compatible during rolling deployment.

- [ ] **Step 1: Write failing AI-status service tests**

Cover no evidence, collecting, ready-to-evaluate, evaluating, published, rejected-latest-with-old-current, reevaluating, corrupt current artifact, Today artifact present/absent, fresh/stale history, and latest BuildAiModel queued/running/failed. Assert an artifact incompatible with the current deterministic algorithm/profile is not reported as published or available. Assert readiness uses Task 01 and deterministic status is not altered.

- [ ] **Step 2: Implement `AiStatusService`**

Load model evidence, current deterministic model/profile, current-compatible and latest AI evaluations, latest `BuildAiModel` job, and freshness concurrently where repository lifetimes allow. Build `AiReadinessLifecycle`; expose only validation summaries and identifiers, never artifacts. Published fields come only from an artifact compatible with the current deterministic algorithm and exact profile. `TodayAvailable` requires that compatible Published Today artifact plus current history; a stale marker leaves the model published but availability false.

- [ ] **Step 3: Write failing model-contract/endpoint tests**

Assert exact camelCase nested shape, all readiness states, optional AI null for compatibility, build job mapping, percentages unrounded in JSON, safe rejection code only, and existing deterministic endpoint tests unchanged.

- [ ] **Step 4: Extend model status composition**

Inject `AiStatusService` into `ModelStatusService` or compose in the endpoint, but perform one authoritative mapping path. Prefer adding `AiStatusResult?` to `ModelStatusResult`, then map both in `ModelsEndpoints`.

- [ ] **Step 5: Write failing manual-confirmation endpoint tests**

Assert authenticated POST with empty body returns server-time confirmation and ManualConfirmation source; anonymous is 401; body timestamp/query timestamp is ignored or rejected rather than accepted; repository/service failure maps to safe 503 `training-history-confirmation-failed`; and a subsequent model status reports current history.

- [ ] **Step 6: Implement and map endpoint**

Map `POST /api/training-history/confirm-current` under the same authorization convention as Training. Call only `TrainingHistoryFreshnessService.ConfirmManualAsync`. Return 200 with response, not 202; it is a single database write and queues no model build.

- [ ] **Step 7: Run service/API/contract regressions**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~AiStatusServiceTests|FullyQualifiedName~ModelStatusServiceTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ModelEndpointTests|FullyQualifiedName~TrainingHistoryEndpointTests|FullyQualifiedName~ProblemDetailsTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 8: Commit and push**

```bash
git add src/RouteTimer.Services/Ai/Readiness src/RouteTimer.Services/Models src/RouteTimer.Contracts/Models src/RouteTimer.Api/Endpoints src/RouteTimer.Api/Program.cs tests
git commit -m "feat: expose AI readiness and history status"
git push
git status --short
```

Expected: successful push and empty status.

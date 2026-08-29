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

## Tasks

Each task lives in its own file. Work through them in order; every task file ends with a step to commit, push, and summarize that task's changes before moving to the next one.

1. [Introduce `PredictionRoute` without changing baseline output](01-introduce-prediction-route.md)
2. [Add the segment-aware power-policy seam](02-power-policy-seam.md)
3. [Define adjustment domain types, contracts, validation, and canonical JSON](03-adjustment-domain-contracts.md)
4. [Persist adjustment aggregates and enforce ownership](04-persist-adjustment-aggregates.md)
5. [Add durable adjustment creation, query, deletion, and job orchestration](05-adjustment-job-orchestration.md)
6. [Expose nested APIs and capabilities](06-nested-apis-and-capabilities.md)
7. [Build the baseline-primary adjustment shell in the client](07-baseline-adjustment-shell.md)
8. [Deliver segment-specific gains end to end](08-segment-specific-gains.md)
9. [Implement bounded full-simulation search and normalized power primitives](09-bounded-search-and-np.md)
10. [Deliver time-target pacing end to end](10-time-target-pacing.md)
11. [Deliver NP/IF targeting end to end](11-np-if-targeting.md)
12. [Deliver FTP and inferred zone targeting end to end](12-ftp-zone-targeting.md)
13. [Deliver variable match-burning end to end](13-variable-match-burning.md)
14. [Add one-adjustment visualization overlays](14-visualization-overlays.md)
15. [Harden lifecycle, limits, and backward compatibility](15-harden-lifecycle-and-limits.md)
16. [Add rollout evidence and complete system verification](16-rollout-evidence-and-verification.md)

## Execution checkpoints

Pause for review after Tasks 2, 6, 8, 11, 13, and 16. Those checkpoints respectively validate baseline parity, the shared resource contract, the first complete vertical slice, the shared search family, the highest-risk strategy, and production readiness. At every checkpoint, use `superpowers:requesting-code-review`; before claiming completion, use `superpowers:verification-before-completion` with fresh command output.

Do not merge implementation as one unreviewed change. The preferred delivery is a sequence of pull requests that keep all new flags off until the required vertical slice is complete. If implementation is executed on one long-lived branch, retain the task commits above so each checkpoint has an auditable review range.

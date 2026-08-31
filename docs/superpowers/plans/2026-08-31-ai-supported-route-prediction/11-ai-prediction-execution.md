[← Plan overview](README.md)

# AI Prediction Execution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Evaluate a captured AI model safely, enforce route/Today gates, rerun physics with the learned effort multiplier, and implement the complete fallback chain.

**Architecture:** A focused runner receives the already-calculated deterministic result. It validates captured model compatibility, extracts route features, evaluates support, optionally derives current state, clamps the additive log multiplier, and invokes the same predictor through `MultiplierPowerTargetPolicy`.

**Tech Stack:** Tasks 02-10, existing route predictor, xUnit and prediction workflow tests.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Calculate deterministic result before any AI repository/evaluator operation.
- AI rerun uses no forecast environment; ordinary predictions remain calm/dry.
- Catch and downgrade AI-specific/model/persistence numerical failures; never catch cancellation or deterministic failure.
- Explicit pacing-adjustment handlers remain unchanged.
- Request code review after this task before Task 12.

### Task 11: Add AI runner and integrate prediction publication

**Files:**

- Create: `src/RouteTimer.Services/Ai/Prediction/AiPredictionOutcome.cs`
- Create: `src/RouteTimer.Services/Ai/Prediction/AiPredictionRunner.cs`
- Create: `src/RouteTimer.Services/Ai/Prediction/AiPredictionException.cs`
- Modify: `src/RouteTimer.Services/Predictions/PredictionJobHandler.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Prediction/AiPredictionRunnerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionWorkflowTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Adjustments/PredictionAdjustmentWorkflowTests.cs`

**Interfaces:**

```csharp
public sealed record AiPredictionOutcome(
    PredictionResult Final,
    PredictionAiPublication Publication);

public sealed class AiPredictionRunner
{
    public Task<AiPredictionOutcome> RunAsync(
        PredictionRoute route,
        RiderProfile profile,
        RiderModelSnapshot riderModel,
        PredictionResult deterministic,
        PredictionMode requestedMode,
        Guid? capturedAiModelId,
        DateTimeOffset predictionCreatedAt,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing model/route fallback tests**

Assert null model ID, missing artifact, rejected artifact, deterministic algorithm mismatch, profile mismatch, invalid artifact, unsupported route, and non-serving stages return the exact same deterministic `PredictionResult` reference with effective Deterministic and the specific fallback. Disabled uses `ai-serving-disabled`, Shadow uses `ai-shadow-mode`, and Comparison uses `ai-comparison-mode`. Assert route gate is evaluated before Today state work.

- [ ] **Step 2: Write failing Typical success tests**

Use a valid linear artifact and supported route. Assert evaluator input is exact Task 03 vector, log prediction is exponentiated, final multiplier intersects global and observed ranges, multiplier policy reruns the full predictor, final segment powers/speeds/times come from rerun, metadata includes model/match/neighbours/errors, and no fallback is present. Assert contributions use fixed-schema codes, omit non-finite/zero effects, sort by descending absolute log effect then schema order, retain at most three, and map positive/negative log effects to `increased-effort`/`reduced-effort`. Assert an empty or invalid bound intersection falls back with `ai-evaluation-fallback`.

- [ ] **Step 3: Write failing Today chain tests**

Cover:

- fresh history + 42 days + Today artifact + supported state => AiToday;
- stale history => AiTypical with `today-history-stale`;
- no Today artifact => AiTypical with `today-model-unavailable`;
- fewer than 42 days => AiTypical with `today-model-unavailable`;
- state outside range => AiTypical with `today-state-unsupported`;
- Typical route unsupported => Deterministic without calculating Today;
- Typical evaluator/rerun failure => Deterministic `ai-evaluation-fallback`.

Verify current state uses only activities with `StartedAt < predictionCreatedAt`, the exact captured rider model for intensity denominator, and confirmation freshness evaluated at `predictionCreatedAt`.

- [ ] **Step 4: Implement runner**

For Automatic stage:

1. load captured AI model by ID;
2. validate Published, versions, deterministic algorithm and exact profile;
3. extract route features and evaluate support;
4. evaluate Typical log adjustment;
5. if Today requested, load weather-ready evidence before creation time, freshness, 42-day state, state ranges, and Today residual;
6. exponentiate sum and clamp to the intersection of global and artifact observed bounds;
7. rerun `IRoutePredictor.Predict(route, profile, riderModel.Model, new MultiplierPowerTargetPolicy(multiplier), ct)` with no environment;
8. retain at most three validated feature contributions using the ordering from Step 2;
9. return final result and complete metadata.

For Disabled or Shadow, do not evaluate. For Comparison, evaluate fully but return/persist deterministic as final while emitting an internal comparison result for Task 15 telemetry; do not expose the unserved time as final metadata.

- [ ] **Step 5: Integrate `PredictionJobHandler`**

Keep parsing/route processing and deterministic calculation in the handler. Call runner afterward. Refactor `BuildPublication` to accept `AiPredictionOutcome`, use `Final` for segments/averages, validate `DeterministicBaselineTime` against the original deterministic result, and persist atomically. Catch only `AiPredictionException`, `InvalidPersistedAiModelException`, and non-cancellation arithmetic/argument errors inside runner, not around the whole job.

- [ ] **Step 6: Protect pacing adjustments**

Run and extend adjustment workflow tests to assert existing adjustment jobs use their persisted baseline segments/model and never resolve `IRiderAiModelRepository`, training freshness, or AI runner. No adjustment contract gains mode or multiplier fields.

- [ ] **Step 7: Run runner, workflow and adjustment regressions**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~AiPredictionRunnerTests|FullyQualifiedName~PredictionWorkflowTests|FullyQualifiedName~PredictionAdjustmentWorkflowTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 8: Commit, push, and request review**

```bash
git add src/RouteTimer.Services/Ai/Prediction src/RouteTimer.Services/Predictions src/RouteTimer.Api/Program.cs tests/RouteTimer.Services.Tests/Ai/Prediction tests/RouteTimer.Services.Tests/Predictions tests/RouteTimer.Services.Tests/Adjustments
git commit -m "feat: apply AI effort to supported predictions"
git push
git status --short
```

Expected: successful push and empty status. Request code review for Tasks 08-11 and resolve findings before Task 12.

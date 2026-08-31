[← Plan overview](README.md)

# Nested Chronological Evaluation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Select Typical and Today additive candidates without leakage, score whole-ride moving time on supported outer folds, and return a publish/reject decision using every approved gate.

**Architecture:** Expanding outer folds begin after the first 14 derived examples. Each outer prefix runs its own expanding inner candidate and route-boundary selection. Whole-ride physics scoring uses the replay context, not multiplier error; final artifacts are fitted only after unbiased outer gates pass.

**Tech Stack:** Task 04 replay/solver, Tasks 05-06 models/support, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- No random shuffling, random cross-validation, or sample-level split.
- Candidate choice, hyperparameters, scaling, Today centring, and support boundary for an outer ride see only its prefix.
- Baseline and AI metrics use the identical supported outer ride IDs.
- Typical may publish without Today; Today may never publish without Typical.
- Request code review after this task before Task 08.

### Task 7: Implement nested chronological challenger evaluation

**Files:**

- Modify: `src/RouteTimer.Domain/Ai/AiValidation.cs`
- Create: `src/RouteTimer.Services/Ai/Training/AiLabeledReplay.cs`
- Create: `src/RouteTimer.Services/Ai/Training/AiValidationMetricsCalculator.cs`
- Create: `src/RouteTimer.Services/Ai/Training/AiCandidateSelector.cs`
- Create: `src/RouteTimer.Services/Ai/Training/AiPublicationGate.cs`
- Create: `src/RouteTimer.Services/Ai/Training/AiChallengerEvaluator.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Training/AiValidationMetricsCalculatorTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Training/AiCandidateSelectorTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Training/AiPublicationGateTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Training/AiChallengerEvaluatorTests.cs`

**Interfaces:**

```csharp
public sealed record AiLabeledReplay(AiHistoricalReplay Replay, EffortLabel Label);

public sealed record AiChallengerEvaluation(
    AiPublicationState PublicationState,
    AiRegressorArtifact? TypicalArtifact,
    AiRegressorArtifact? TodayArtifact,
    AiRouteSupportArtifact? RouteSupport,
    AiStateSupportRanges? TodayStateSupport,
    AiValidationMetrics? DeterministicMetrics,
    AiValidationMetrics? TypicalMetrics,
    AiValidationMetrics? TodayMetrics,
    double ObservedMinimumMultiplier,
    double ObservedMaximumMultiplier,
    string? RejectionReason);

public sealed class AiChallengerEvaluator
{
    public AiChallengerEvaluation Evaluate(
        IReadOnlyList<AiLabeledReplay> chronologicalExamples,
        CancellationToken cancellationToken);
}
```

Stable rejection codes include `ai-insufficient-derived-examples`, `ai-insufficient-supported-folds`, `ai-typical-median-not-improved`, `ai-typical-p90-regressed`, `ai-typical-biased`, `ai-route-support-calibration-failed`, and `ai-serving-range-empty`. An insufficient-example rejection may have null metrics; Published requires all deterministic/Typical metrics. Today failure is stored separately as a `today-*` diagnostic but does not reject Typical.

- [ ] **Step 1: Write failing metric/publication-boundary tests**

Lock percentile interpolation, APE/signed error formulas, exact 10% relative improvement, exact one-point absolute improvement, P90 equality, exact ±3% bias, 14 versus 15 folds, finite validation, and serving-range intersection with `[0.75,1.25]`. Every threshold needs below/equal/above cases.

- [ ] **Step 2: Implement metrics and publication gate**

Actual seconds must be positive. Sort copies, never mutate fold order. Gate in stable order: count, finite values, relative median, absolute median, P90, bias, support, serving range; return the first stable rejection code.

- [ ] **Step 3: Write failing candidate-selector tests**

Create synthetic chronological examples and a fake whole-ride scorer. Assert inner training begins with eight prior examples, each candidate is retrained for each inner fold, lowest inner median APE wins, ties use `StableOrder`, Today target is `label.LogMultiplier - typicalPredictionForThatTrainingRow`, Today rows without 42-day history are excluded, and no candidate sees the inner target before predicting it.

- [ ] **Step 4: Implement inner selection**

For Typical, train each Task 05 catalog candidate on each inner prefix and call `EffortMultiplierSolver.Simulate` on the next replay to calculate time error. For Today, first fit the already-selected Typical candidate on that inner prefix, calculate route residual targets for prefix rows, then select a state candidate with the same expanding procedure. Re-centre each fitted Today artifact by subtracting its prediction at its captured raw feature medians from its intercept, so neutral long-term state evaluates to exactly zero. Return selected definitions, not fitted artifacts.

- [ ] **Step 5: Write failing full nested-evaluation tests**

Use 29 derived examples corresponding to 30 rides: first 14 seed, next 15 outer. Record every activity ID passed to trainer, support calibrator, and scorer. Assert:

- outer target never appears in any prefix call;
- inner support calibration precedes and gates each outer result;
- unsupported outer rides are recorded but excluded from both baseline and AI metrics;
- fewer than 15 supported outer folds rejects publication;
- a synthetic Typical relationship publishes with exact artifacts and metrics;
- Today publishes only with 15 supported, 42-day-applicable folds and passing state ranges;
- Today failure leaves Typical published with null Today artifacts;
- predicted outer multiplier bounds produce a non-empty serving intersection; and
- final model/support fitting happens after outer scoring and cannot change recorded outer metrics.

- [ ] **Step 6: Implement `AiChallengerEvaluator`**

Sort by `(Replay.StartedAt, Replay.ActivityId)`. Require 29 derived examples and use indices `0..13` as seed, `14..end` as outer folds. In each outer prefix:

1. select Typical candidate using inner expanding folds;
2. train it on the entire prefix;
3. build inner support observations and calibrate the prefix-only gate;
4. gate the unseen outer route;
5. when supported, simulate baseline log multiplier 0 and AI predicted multiplier and store identical-ID errors;
6. independently select/train/evaluate Today for eligible state rows.

After at least 15 supported outer folds pass publication gates, select candidates once more from all examples, fit final Typical, fit Today to residual targets if Today passed, fit final route support from all derived examples using the same method, calculate Today supported ranges from successful outer folds, and return immutable artifacts. Never catch cancellation.

- [ ] **Step 7: Run focused training tests and replay regressions**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~AiValidationMetricsCalculatorTests|FullyQualifiedName~AiCandidateSelectorTests|FullyQualifiedName~AiPublicationGateTests|FullyQualifiedName~AiChallengerEvaluatorTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~EffortMultiplierSolverTests|FullyQualifiedName~HistoricalBaselineReplayerTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 8: Commit, push, and request review**

```bash
git add src/RouteTimer.Domain/Ai src/RouteTimer.Services/Ai/Training tests/RouteTimer.Services.Tests/Ai/Training
git commit -m "feat: validate AI challengers chronologically"
git push
git status --short
```

Expected: successful push and empty status. Request code review for Tasks 05-07 and resolve findings before Task 08.

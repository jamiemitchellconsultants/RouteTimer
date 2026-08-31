[← Plan overview](README.md)

# Route and State Support Gates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permit learned predictions only for routes and Today states supported by held-out evidence, and report comparable validation error without calling similarity a probability.

**Architecture:** A fixed subset of route features is robustly scaled. Fifth-nearest Euclidean distance supplies evidence density; inner-validation outcomes calibrate the largest safe boundary. Separate inclusive Today ranges reject state extrapolation.

**Tech Stack:** Pure Domain/Services C#, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- A route requires five neighbours; fewer is always unsupported.
- Route-match percentage is similarity only, never probability/confidence.
- Today state outside any observed supported range falls back; do not clamp it.
- Calibration sees only rows earlier than the outer target; Task 07 enforces that calling boundary.

### Task 6: Add route-neighbour and Today-state support artifacts

**Files:**

- Modify: `src/RouteTimer.Domain/Ai/AiModelArtifacts.cs`
- Create: `src/RouteTimer.Domain/Ai/AiValidation.cs`
- Create: `src/RouteTimer.Services/Ai/Support/AiSupportDistance.cs`
- Create: `src/RouteTimer.Services/Ai/Support/AiRouteSupportCalibrator.cs`
- Create: `src/RouteTimer.Services/Ai/Support/AiRouteSupportEvaluator.cs`
- Create: `src/RouteTimer.Services/Ai/Support/AiStateSupportCalculator.cs`
- Create: `tests/RouteTimer.Domain.Tests/Ai/AiSupportArtifactTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Support/AiSupportDistanceTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Support/AiRouteSupportCalibratorTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Support/AiRouteSupportEvaluatorTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Support/AiStateSupportCalculatorTests.cs`

**Interfaces:**

```csharp
public sealed record AiTimeError(
    double ActualSeconds,
    double PredictedSeconds,
    double AbsolutePercentageError,
    double SignedPercentageError);

public sealed record AiSupportPoint(Guid ActivityId, AiFeatureVector RouteFeatures);
public sealed record AiSupportValidationPoint(
    Guid ActivityId,
    AiFeatureVector RouteFeatures,
    double FifthNeighbourDistance,
    AiTimeError DeterministicError,
    AiTimeError AiError);

public sealed record AiCriticalFeatureRanges(
    double MinimumBaselineSeconds, double MaximumBaselineSeconds,
    double MinimumAscentPerKilometre, double MaximumAscentPerKilometre,
    double MinimumSteepShare, double MaximumSteepShare,
    double MinimumDescentCurvatureP90, double MaximumDescentCurvatureP90);

public sealed record AiRouteSupportArtifact(
    IReadOnlyList<double> Medians,
    IReadOnlyList<double> Scales,
    IReadOnlyList<AiSupportPoint> TrainingPoints,
    IReadOnlyList<AiSupportValidationPoint> ValidationPoints,
    double MaximumFifthNeighbourDistance,
    AiCriticalFeatureRanges CriticalRanges);

public sealed record AiStateSupportRanges(
    IReadOnlyList<double> Minimums,
    IReadOnlyList<double> Maximums);
```

Similarity dimensions are fixed Typical indices `4, 0, 3, 6, 7, 9, 10, 12`: baseline seconds, distance, ascent/km, four outer grade shares, and descent-curvature P90.

- [ ] **Step 1: Write failing domain validation tests**

Assert defensive copies, exact dimension counts, positive scales/boundary, at least five training points, finite ordered ranges, matching Today min/max counts, known schema versions, and validation points with non-negative finite distances/errors.

- [ ] **Step 2: Implement support domain records**

Put validation in constructors. Add stable reason constants: `ai-route-neighbour-support-insufficient`, `ai-route-duration-unsupported`, `ai-route-climbing-unsupported`, `ai-route-grade-unsupported`, `ai-route-curvature-unsupported`, and `today-state-unsupported`.

- [ ] **Step 3: Write failing distance tests**

Assert selected-index extraction, median/IQR scaling, Euclidean distance, zero-IQR scale 1, stable tie ordering by activity ID, fifth-neighbour selection, and rejection with only four prior points.

- [ ] **Step 4: Implement distance helper**

Fit scaling only from the training points supplied by the caller. Sort neighbours by distance then activity ID. Return both the nearest five and fifth distance; never use later validation points to fit scaling.

- [ ] **Step 5: Write failing calibration/evaluation tests**

Provide at least five inner-validation observations at several fifth distances. For every sorted unique candidate threshold, select observations at/below it. A threshold is eligible with at least five observations and only when AI achieves the spec's relative/absolute median improvement, P90 non-degradation, and median bias limit. Assert the largest eligible threshold wins, no eligible threshold returns `ai-route-support-calibration-failed`, critical ranges come from training points, exact boundaries pass, and one-tick outside fails with the specific reason.

- [ ] **Step 6: Implement calibration and runtime decision**

Use the percentile definition from `ModelValidator`. Match percentage is `100 * (1 - fifthDistance / maximumDistance)`, clamped to `[0,100]`. Comparable errors use up to five validation points nearest to the request among points within the boundary; report their median and P90 AI APE. If route support fails, both error values and match are null.

- [ ] **Step 7: Write failing Today range tests and implement**

Calculate per-index inclusive min/max from supported Today outer folds only. Require at least one row and exact 10-value schema. Assert exact boundaries pass; non-finite, count mismatch, or any value outside returns `today-state-unsupported` with no partially clamped vector.

- [ ] **Step 8: Run focused support tests**

```bash
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore --filter FullyQualifiedName~AiSupportArtifactTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~AiSupportDistanceTests|FullyQualifiedName~AiRouteSupportCalibratorTests|FullyQualifiedName~AiRouteSupportEvaluatorTests|FullyQualifiedName~AiStateSupportCalculatorTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 9: Commit and push**

```bash
git add src/RouteTimer.Domain/Ai src/RouteTimer.Services/Ai/Support tests/RouteTimer.Domain.Tests/Ai tests/RouteTimer.Services.Tests/Ai/Support
git commit -m "feat: gate AI predictions by evidence support"
git push
git status --short
```

Expected: successful push and empty status.

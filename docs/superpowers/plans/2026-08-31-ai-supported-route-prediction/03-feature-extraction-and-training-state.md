[← Plan overview](README.md)

# Feature Extraction and Training State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert a route/baseline prediction and strictly earlier rides into fixed, versioned Typical and Today feature vectors.

**Architecture:** Named schema classes own fixed feature order. Pure extractors aggregate processed geometry and prediction output; training-state calculation uses exponentially decayed earlier ride totals and never reads the target outcome.

**Tech Stack:** RouteTimer Domain/Services, weather-resolved activity evidence, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Feature order is a persisted API. Do not build vectors from dictionary enumeration.
- Coordinates, names, upload source, equipment IDs, absolute date, target actual power/time, heart rate, and cadence are forbidden.
- Every vector value must be finite. Invalid input excludes an example with a stable reason; it is never replaced with zero unless zero is the defined physical value.
- Today uses only activities with `StartedAt < targetAt`.

### Task 3: Add fixed feature schemas and leak-free extraction

**Files:**

- Create: `src/RouteTimer.Domain/Ai/AiFeatureVector.cs`
- Create: `src/RouteTimer.Services/Ai/Features/AiTypicalFeatureSchema.cs`
- Create: `src/RouteTimer.Services/Ai/Features/AiTodayFeatureSchema.cs`
- Create: `src/RouteTimer.Services/Ai/Features/AiRouteFeatureExtractor.cs`
- Create: `src/RouteTimer.Services/Ai/Features/TrainingStateCalculator.cs`
- Create: `src/RouteTimer.Services/Ai/Features/AiFeatureExtractionException.cs`
- Create: `tests/RouteTimer.Domain.Tests/Ai/AiFeatureVectorTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Features/AiRouteFeatureExtractorTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Features/TrainingStateCalculatorTests.cs`

**Interfaces:**

```csharp
public static class AiTypicalFeatureSchema
{
    public static IReadOnlyList<string> Names { get; }
    public static int Count => 16;
}

public static class AiTodayFeatureSchema
{
    public static IReadOnlyList<string> Names { get; }
    public static int Count => 10;
}

public sealed class AiRouteFeatureExtractor
{
    public AiFeatureVector Extract(
        PredictionRoute route,
        PredictionResult deterministic,
        RiderModel deterministicModel);
}

public sealed class TrainingStateCalculator
{
    public AiTrainingState Calculate(
        DateTimeOffset targetAt,
        IReadOnlyList<WeatherActivityEvidence> strictlyEarlierActivities,
        RiderModel earlierModel);
}
```

Typical feature order is exactly: distance metres, ascent metres, descent metres, ascent metres/km, baseline seconds, baseline average watts, below-6% distance share, -6:-3% share, -3:3% share, 3:6% share, above-6% share, descent-curvature median, descent-curvature P90, low-confidence-or-extrapolated time share, calibrated flag, learned-descent flag.

Today order is exactly: decayed 7-day hours, decayed 42-day hours, decayed 7-day kJ, decayed 42-day kJ, 7-day ride count, 42-day ride count, 7-day active-day count, 42-day active-day count, days since last ride, recent intensity ratio.

- [ ] **Step 1: Write failing vector/schema tests**

Assert defensive copies, exact schema version, exact ordered names/counts above, rejection of null/non-finite values, and rejection when a vector count does not match the selected schema. The test must explicitly lock all 16 Typical and 10 Today names.

- [ ] **Step 2: Implement feature-vector validation and schemas**

Use arrays exposed through `Array.AsReadOnly`. Add `AiFeatureVector.ForTypical` and `.ForToday` factories so arbitrary callers cannot pair a Today count with a Typical schema.

- [ ] **Step 3: Write failing route-feature tests**

Build a route with known segment distances, elevation changes, gradients, curvature, baseline powers/times/confidence, and one extrapolation warning. Assert weighted shares sum to 1, descent equals sum of negative elevation differences, weighted average watts uses segment seconds, curvature statistics use descending segments only, empty descent gives zero, and no coordinate/name/date appears in the vector. Assert non-finite, sequence mismatch, zero distance/time, and inconsistent segment totals throw `AiFeatureExtractionException` with a stable code.

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~AiRouteFeatureExtractorTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: FAIL because the extractor does not exist.

- [ ] **Step 4: Implement Typical extraction**

Use distance weights for gradient shares and time weights for power/confidence shares. The low-confidence-or-extrapolated share counts segment time when confidence is Low or the baseline result contains the power-model extrapolation warning. The final two flags are `1/0` for `WasCalibrated` and whether any descent cell is learned. Reuse the route and prediction validation conventions from `PredictionJobHandler`; do not weaken them.

- [ ] **Step 5: Write failing training-state tests**

Cover strict exclusion at `StartedAt == targetAt`, later rides, discontinuities, missing power, invalid sample gaps, trapezoidal mechanical work, 7/42 exponential decay `exp(-ageDays/tauDays)`, distinct UTC active days, zero rides, genuine long rest, and exactly 42 calendar days between earliest retained earlier ride and target. Calculate recent intensity as decayed 7-day mean recorded watts divided by `earlierModel.PowerModel.GlobalTypicalWatts`; reject a non-positive denominator.

- [ ] **Step 6: Implement Today state calculation**

For each continuous adjacent sample pair with both powers and `0 < seconds <= 10`, add `(p0+p1)/2 * seconds / 1000` kJ. Treat each ride total as occurring at `Metadata.EndedAt` for decay age. Count features are unweighted counts inside the named windows; moving hours and work use exponential decay. `HasFortyTwoDaysHistory` means the earliest retained earlier activity starts at least 42 days before target, not that a ride occurred recently. With no earlier ride, set days-since-last-ride to 42 and other numeric state values to zero, but set `HasFortyTwoDaysHistory=false`.

- [ ] **Step 7: Run focused and service regressions**

```bash
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore --filter FullyQualifiedName~AiFeatureVectorTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~AiRouteFeatureExtractorTests|FullyQualifiedName~TrainingStateCalculatorTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 8: Commit and push**

```bash
git add src/RouteTimer.Domain/Ai src/RouteTimer.Services/Ai/Features tests/RouteTimer.Domain.Tests/Ai tests/RouteTimer.Services.Tests/Ai/Features
git commit -m "feat: extract AI route and training features"
git push
git status --short
```

Expected: successful push and empty status.

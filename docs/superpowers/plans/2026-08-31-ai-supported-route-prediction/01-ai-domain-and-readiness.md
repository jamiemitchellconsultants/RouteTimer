[← Plan overview](README.md)

# AI Domain and Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish the stable AI domain vocabulary and calculate the transparent 50/25/25 evidence-readiness score without training a model.

**Architecture:** Immutable enums/records live in Domain. A pure service class filters eligible weather-ready rides, classifies duration and terrain, calculates partial bucket credit, and selects stable evidence-strength/suggestion codes.

**Tech Stack:** .NET 10, C# 14, xUnit, completed weather evidence contracts.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Follow the overview's Global Constraints and stable constants.
- Keep `RouteTimer.Domain` free of Services, EF Core, HTTP, and JSON dependencies.
- Only `ActivityEligibility.Eligible` plus `WeatherEnrichmentState.Ready` counts.
- Readiness permits evaluation; it never represents model publication or prediction confidence.

### Task 1: Add AI vocabulary and readiness calculation

**Files:**

- Create: `src/RouteTimer.Domain/Ai/AiAlgorithmVersions.cs`
- Create: `src/RouteTimer.Domain/Ai/AiReadiness.cs`
- Create: `src/RouteTimer.Domain/Predictions/PredictionMode.cs`
- Create: `tests/RouteTimer.Domain.Tests/Ai/AiReadinessValueTests.cs`
- Create: `src/RouteTimer.Services/Ai/Readiness/AiEvidenceClassifier.cs`
- Create: `src/RouteTimer.Services/Ai/Readiness/AiReadinessCalculator.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Readiness/AiReadinessCalculatorTests.cs`

**Interfaces:**

```csharp
public enum AiDurationBucket { UnderOneHour, OneToTwoHours, TwoToFourHours, FourHoursOrMore }
public enum AiTerrainBucket { Flat, Rolling, ClimbingIntensive }

public sealed record AiReadinessLifecycle(
    bool BuildRunning,
    bool HasPublishedTypical,
    bool LatestCompletedWasRejected);

public static class AiEvidenceClassifier
{
    public static AiDurationBucket Duration(TimeSpan movingTime);
    public static AiTerrainBucket Terrain(double distanceMetres, double ascentMetres);
}

public sealed class AiReadinessCalculator
{
    public AiReadinessSnapshot Calculate(
        IReadOnlyList<TrainingActivityModelEvidence> evidence,
        AiReadinessLifecycle lifecycle);
}
```

Use the enums and readiness records from `README.md` exactly. Stable message codes are `duration-under-1h`, `duration-1-2h`, `duration-2-4h`, `duration-4h-plus`, `terrain-flat`, `terrain-rolling`, `terrain-climbing`, and `add-eligible-ride`.

- [ ] **Step 1: Write failing domain validation tests**

Assert `AiReadinessContributor` rejects negative counts, `Current > Target`, negative/non-finite points, and points above maximum. Assert `AiReadinessSnapshot` rejects percentage outside `[0,100]`, contributor maximums other than `50/25/25`, and `CanEvaluate=true` below the evidence gate. Assert every enum value is defined and Prediction mode defaults are not inferred from arbitrary strings.

Run:

```bash
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore --filter FullyQualifiedName~AiReadinessValueTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: FAIL because `RouteTimer.Domain.Ai` and `PredictionMode` do not exist.

- [ ] **Step 2: Implement immutable domain values and constants**

Use constructor bodies, defensive list copies where applicable, `double.IsFinite`, and `Enum.IsDefined`. Add all stable constants from the overview. Do not add UI text to Domain.

- [ ] **Step 3: Write failing classification and scoring tests**

Create synthetic `TrainingActivityModelEvidence` fixtures and cover:

- exact duration boundaries at 1, 2, and 4 hours;
- terrain boundaries at exactly 7 and 15 ascent metres/km;
- zero/non-finite distance or ascent excluded from terrain credit but still eligible for count/duration;
- ineligible, Pending, Failed, and Unavailable rides excluded entirely;
- 30 qualifying rides with two duration and two terrain buckets sets `CanEvaluate=true`;
- 29 rides, or only one supported duration/terrain bucket, remains false;
- 60 rides gives 50 count points;
- each bucket earns `bucketMaximum * min(count / 3d, 1)`;
- score is the sum, rounded only for display by clients, never inside the calculation;
- strongest code uses highest supported ride count with duration order before terrain for ties;
- next code selects the largest missing point contribution with stable enum order for ties; and
- lifecycle maps to every readiness state from the accepted spec.

Example assertion:

```csharp
[Fact]
public void Calculate_does_not_confuse_enough_evidence_with_publication()
{
    var result = Calculator.Calculate(ThirtyVariedReadyRides(), new(false, false, true));
    Assert.True(result.CanEvaluate);
    Assert.Equal(AiReadinessState.BaselineStillBest, result.State);
}
```

- [ ] **Step 4: Implement classification and readiness**

Use metadata distance/ascent when finite and positive. Count supported buckets with at least three qualifying rides. Duration maximum per bucket is `25/4`; terrain maximum is `25/3`. `Reevaluating` requires both build-running and a published Typical model; otherwise build-running is `Evaluating`. With no running build, published wins over latest rejection, then rejection, ready-to-evaluate, and collecting evidence.

- [ ] **Step 5: Run focused and layer regressions**

```bash
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~AiReadiness -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: all commands exit 0.

- [ ] **Step 6: Commit and push**

```bash
git add src/RouteTimer.Domain/Ai src/RouteTimer.Domain/Predictions/PredictionMode.cs src/RouteTimer.Services/Ai/Readiness tests/RouteTimer.Domain.Tests/Ai tests/RouteTimer.Services.Tests/Ai/Readiness
git commit -m "feat: calculate AI training readiness"
git push -u origin HEAD
git status --short
```

Expected: push succeeds and status is empty. If upstream already exists, use `git push`.

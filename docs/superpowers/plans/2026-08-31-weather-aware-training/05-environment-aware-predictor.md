[← Plan overview](README.md)

# Environment-Aware Predictor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an optional weather environment to route simulation while preserving the no-environment result exactly.

**Architecture:** Route segments carry bearing. An optional `PredictionEnvironment` resolves weather at absolute simulated time; force integration uses signed apparent air velocity, and wetness only modifies descent caps/confidence/warnings.

**Tech Stack:** RouteTimer domain/services, deterministic physics integration, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- The existing call without environment is the authoritative regression path and must remain bit-for-bit equal at the public record boundary.
- Weather never changes target watts or the rider model.
- Rain threshold and descent multiplier come from `PredictionEnvironment`, not global mutable state.
- Pacing-adjustment call sites pass no environment.

### Task 5: Add the predictor environment seam

**Files:**

- Create: `src/RouteTimer.Services/Predictions/IRouteEnvironment.cs`
- Modify: `src/RouteTimer.Domain/Predictions/PredictionRoute.cs`
- Modify: `src/RouteTimer.Domain/Predictions/PredictionWarningCodes.cs`
- Modify: `src/RouteTimer.Services/Predictions/IRoutePredictor.cs`
- Modify: `src/RouteTimer.Services/Predictions/RoutePredictor.cs`
- Modify: `src/RouteTimer.Services/Physics/CyclingForces.cs`
- Modify: `tests/RouteTimer.Services.Tests/Adjustments/PacingStrategyBacktestingTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/ModelValidatorTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionWorkflowTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Physics/CyclingForcesWeatherTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/RoutePredictorTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionFixtures.cs`

**Interfaces:**

```csharp
public interface IRouteEnvironment
{
    WeatherCondition Resolve(PredictionRouteSegment segment, DateTimeOffset at);
}

public sealed record PredictionEnvironment(
    DateTimeOffset StartAt,
    IRouteEnvironment Conditions,
    double WetThresholdMillimetres,
    double WetDescentMultiplier);

PredictionResult Predict(
    PredictionRoute route,
    RiderProfile profile,
    RiderModel model,
    IPowerTargetPolicy? powerTargetPolicy = null,
    CancellationToken cancellationToken = default,
    PredictionEnvironment? environment = null);
```

Append `double BearingDegrees = 0` to `PredictionRouteSegment`. `FromProcessed` computes bearing from each original route sample to its segment endpoint, so the first simulated segment uses the skipped leading point. Stored-segment reconstruction may pass `0` because ordinary/pacing simulations supply no environment.

Add:

```csharp
public static double LongitudinalAerodynamicForce(
    double groundSpeedMetresPerSecond,
    double bearingDegrees,
    WindVector windTo,
    double airDensity,
    double cdA);
```

- [ ] **Step 1: Write failing force tests**

Assert zero wind equals `AerodynamicForce`; 5 m/s headwind at 10 m/s equals still-air drag at 15 m/s; 5 m/s tailwind equals 5 m/s; pure crosswind uses vector length and the along-course projection; tailwind faster than rider returns negative force; invalid values throw.

- [ ] **Step 2: Implement signed longitudinal force**

Construct rider ground vector from bearing, calculate `vAir = vGround - vWindTo`, and return `0.5 * rho * cdA * |vAir| * dot(vAir, heading)`. Preserve sign. Do not clamp negative aerodynamic force.

- [ ] **Step 3: Write the calm-regression test before changing predictor code**

Retain `PredictionRoute_refactor_preserves_the_complete_baseline_result` and add a test comparing `environment: null` with a reference condition environment whose wind is zero, precipitation zero, and density exactly equals the model coefficient. Compare confidence, warnings, moving time, and every segment field.

- [ ] **Step 4: Add bearing and environment types**

Validate UTC start, thresholds, and multiplier `(0,1]`. Validate bearing finite and normalize into `[0,360)`. Keep `CancellationToken` in its existing fifth parameter position and append `environment` last, so existing positional pacing calls remain source-compatible. Update the three test `IRoutePredictor` fakes named in Files and all `PredictionRouteSegment` positional construction compile errors explicitly; do not rely on silently shifted arguments.

- [ ] **Step 5: Write failing weather predictor tests**

Use a single straight segment and fixed environment. Assert headwind is slower than calm, tailwind faster, lower-density warm air faster than higher-density cold air, wet descent cap is exactly `baseCap * 0.85`, wet confidence drops one level with Low floor, and `wet-weather` appears once across multiple wet segments. Assert environmental lookup times equal `StartAt + elapsed at segment start` and power watts equal calm output.

- [ ] **Step 6: Thread weather through `RoutePredictor`**

Resolve one `WeatherCondition` at each segment start using absolute simulated time. In every substep, calculate environmental air density via `WeatherMath.AirDensity` and signed force via the new helper. With `environment == null`, execute the existing aerodynamic call and existing descent logic exactly, avoiding a recalculated density that would introduce floating-point drift.

For wet segments, multiply only finite descent caps, lower segment confidence by one enum level, and add `PredictionWarningCodes.WetWeather`. Do not change Crr, power lookup, power policy, or model state.

- [ ] **Step 7: Run predictor, adjustment, and GPX regressions**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~RoutePredictorTests|FullyQualifiedName~CyclingForcesWeatherTests|FullyQualifiedName~PredictionAdjustmentWorkflowTests|FullyQualifiedName~PredictionGpxWriterTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: pass, including the existing golden baseline result.

- [ ] **Step 8: Commit and push**

```bash
git add src/RouteTimer.Domain/Predictions src/RouteTimer.Services/Predictions src/RouteTimer.Services/Physics tests/RouteTimer.Services.Tests
git commit -m "feat: add weather environment to route simulation"
git push
git status --short
```

Expected: successful push and empty status.

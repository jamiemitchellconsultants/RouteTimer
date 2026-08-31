[← Plan overview](README.md)

# Historical Replay and Effort Labels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recreate each ride using only its earlier weather-ready prefix and solve the bounded rider-effort multiplier that reproduces recorded moving time.

**Architecture:** Extract deterministic model assembly from the weather-aware build handler into a shared factory. Historical replay constructs the held-out route/environment and returns an ephemeral scoring context; a bounded bisection solver reruns physics through a multiplier power policy.

**Tech Stack:** Weather-aware RouteTimer services, deterministic physics simulator, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Prefix means strictly earlier by `(StartedAt, ActivityId)` stable ordering.
- The held-out ride cannot enter power, calibration, descent, route-support, or training-state inputs.
- Replay uses held-out historical weather and actual moving time only as the solver label.
- Solver range is `[0.50,1.50]`; a root at either bound is excluded.
- Request code review after this task before starting Task 05.

### Task 4: Share model assembly and derive weather-corrected labels

**Files:**

- Create: `src/RouteTimer.Services/Models/IWeatherAwareRiderModelFactory.cs`
- Create: `src/RouteTimer.Services/Models/WeatherAwareRiderModelFactory.cs`
- Modify: `src/RouteTimer.Services/Models/BuildModelJobHandler.cs`
- Create: `src/RouteTimer.Services/Ai/Replay/MultiplierPowerTargetPolicy.cs`
- Create: `src/RouteTimer.Services/Ai/Replay/EffortMultiplierSolver.cs`
- Create: `src/RouteTimer.Services/Ai/Replay/AiHistoricalReplay.cs`
- Create: `src/RouteTimer.Services/Ai/Replay/HistoricalBaselineReplayer.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Create: `tests/RouteTimer.Services.Tests/Models/WeatherAwareRiderModelFactoryTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/BuildModelJobHandlerTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Replay/EffortMultiplierSolverTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Replay/HistoricalBaselineReplayerTests.cs`

**Interfaces:**

```csharp
public interface IWeatherAwareRiderModelFactory
{
    RiderModel Build(
        RiderProfile profile,
        IReadOnlyList<WeatherActivityEvidence> evidence,
        string algorithmVersion);
}

public sealed class MultiplierPowerTargetPolicy(double multiplier) : IPowerTargetPolicy;

public sealed record AiHistoricalReplay(
    Guid ActivityId,
    DateTimeOffset StartedAt,
    RiderProfile Profile,
    RiderModel PrefixModel,
    PredictionRoute Route,
    PredictionEnvironment Environment,
    PredictionResult Deterministic,
    TimeSpan ActualMovingTime,
    AiFeatureVector TypicalFeatures,
    AiTrainingState TrainingState);

public sealed record EffortLabel(double Multiplier, double LogMultiplier);

public sealed class EffortMultiplierSolver(IRoutePredictor predictor)
{
    public EffortLabel Solve(AiHistoricalReplay replay, CancellationToken cancellationToken);
    public PredictionResult Simulate(
        AiHistoricalReplay replay,
        double logMultiplier,
        CancellationToken cancellationToken);
}

public sealed class HistoricalBaselineReplayer
{
    public AiHistoricalReplay Replay(
        RiderProfile profile,
        IReadOnlyList<WeatherActivityEvidence> earlier,
        WeatherActivityEvidence heldOut,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing shared-factory tests**

Assert raw `.Activity` enters `IPowerModelBuilder`, resolved evidence enters physics/descent, calibration precedes descent, invalid/no evidence uses the same stable errors as the post-weather build handler, and the result carries the supplied algorithm version. Update build-handler tests to expect one factory call plus unchanged weather gating, validation, progress, and save behaviour.

- [ ] **Step 2: Extract and use the model factory**

Move only deterministic component assembly. Keep weather Pending/Ready gating, progress, validation, and persistence in `BuildModelJobHandler`. Register `IWeatherAwareRiderModelFactory` as a singleton in `Program.cs`. Run existing build tests immediately to prove the refactor is behaviour-preserving.

- [ ] **Step 3: Write failing multiplier-policy and solver tests**

Assert multiplier 1 returns baseline watts exactly, non-positive/non-finite constructor values fail, confidence/extrapolation are preserved, synthetic monotonic predictors recover `0.80`, `1.00`, and `1.20` within `1e-6`, cancellation propagates, no bracket throws `EffortLabelException("ai-effort-label-unsolved")`, and a solution within `1e-6` of either search bound throws `ai-effort-label-at-bound`.

- [ ] **Step 4: Implement policy and bounded bisection**

The policy multiplies `context.Baseline.Watts` and returns the same confidence/extrapolated fields. Evaluate both bounds first. Moving time must be longer at the lower multiplier than at the upper multiplier, and actual time must lie strictly between; reject equal, reversed, non-finite, or unbracketed results. Stop when absolute time error is at most one second or after 80 iterations. Return `Math.Log(multiplier)`.

- [ ] **Step 5: Write failing replay tests**

Use at least four weather-ready rides with deliberately different watts/wind. Assert stable `(StartedAt, ActivityId)` prefix ordering, held-out exclusion by ID even for structurally equal rides, prefix factory input, held-out `TimelineRouteEnvironment`, `PredictionEnvironment.StartAt == heldOut.Activity.Metadata.StartedAt`, deterministic prediction with multiplier 1, target actual duration, extracted route features, strictly earlier training state, and no replay for the first ride or an invalid prefix.

- [ ] **Step 6: Implement historical replay**

Process held-out positions with production `IRouteProcessor`, convert to `PredictionRoute`, build the prefix model, construct held-out environment using weather option thresholds, run the deterministic predictor, then invoke Task 03 extractors. Translate route/model/prediction failures into `AiReplayException` stable codes so Task 09 can persist an exclusion rather than fail the whole build.

- [ ] **Step 7: Run focused and deterministic regressions**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~WeatherAwareRiderModelFactoryTests|FullyQualifiedName~BuildModelJobHandlerTests|FullyQualifiedName~EffortMultiplierSolverTests|FullyQualifiedName~HistoricalBaselineReplayerTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~ModelValidatorTests|FullyQualifiedName~RoutePredictorTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 8: Commit, push, and request review**

```bash
git add src/RouteTimer.Services/Models src/RouteTimer.Services/Ai/Replay src/RouteTimer.Api/Program.cs tests/RouteTimer.Services.Tests/Models tests/RouteTimer.Services.Tests/Ai/Replay
git commit -m "feat: derive weather-corrected effort labels"
git push
git status --short
```

Expected: successful push and empty status. Request code review for Tasks 01-04 and resolve findings before Task 05.

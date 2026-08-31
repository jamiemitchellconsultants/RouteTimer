[← Plan overview](README.md)

# Weather-Aware Descent Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Learn dry, calm-equivalent descent caps while rejecting wet and non-normalizable crosswind evidence.

**Architecture:** A bounded solver converts a weather-affected interval into the speed that would yield the same force balance in calm air. The descent builder filters first, normalizes second, and then reuses its existing cells, P90, shrinkage, caps, and fallbacks.

**Tech Stack:** C# numerical code, existing descent model, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Existing calm builder results remain unchanged through its existing overload.
- Exclude wet intervals at `>= wetThreshold` and strong crosswind at `abs(crosswind) > threshold`.
- Never fabricate missing power or accept a non-finite/unbracketed calm solution.
- Preserve the existing nine-cell order, evidence thresholds, P90 interpolation, shrinkage, hard caps, confidence, and fallback provenance.

### Task 7: Normalize descent evidence to dry calm conditions

**Files:**

- Create: `src/RouteTimer.Services/Models/CalmEquivalentSpeedSolver.cs`
- Modify: `src/RouteTimer.Services/Models/IDescentLimitBuilder.cs`
- Modify: `src/RouteTimer.Services/Models/DescentLimitBuilder.cs`
- Create: `tests/RouteTimer.Services.Tests/Models/CalmEquivalentSpeedSolverTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/DescentLimitBuilderTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/BuildModelJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/ModelValidatorTests.cs`

**Interfaces:**

```csharp
public sealed class CalmEquivalentSpeedSolver
{
    public bool TrySolve(
        WeatherResolvedSample start,
        WeatherResolvedSample end,
        RiderProfile profile,
        PhysicalCoefficients coefficients,
        out double calmSpeedMetresPerSecond);
}

DescentLimitModel Build(
    RiderProfile profile,
    IReadOnlyList<WeatherResolvedActivity> activities,
    PhysicalCoefficients coefficients,
    double wetThresholdMillimetres,
    double strongCrosswindMetresPerSecond);
```

Retain `Build(IReadOnlyList<CleanedActivity>)` for existing tests/call sites until Task 08 switches orchestration. Update `FakeDescentLimitBuilder` implementations in the two named model test files so both overloads record inputs independently.

- [ ] **Step 1: Write failing bounded-solver tests**

Use intervals generated from known force balance. Assert zero wind returns observed midpoint speed; headwind produces a calm-equivalent speed greater than observed for the same effort/acceleration; tailwind produces lower; missing start/end power returns false; invalid duration/grade/speed/weather returns false; and an unbracketed solution returns false without throwing.

The solved equation over candidate calm speed `v` is:

```text
wheelPower / max(v, 0.5)
  - gravity(grade)
  - rolling(grade, mass, Crr)
  - aerodynamicStillAir(v, referenceDensity, CdA)
  - mass * observedAcceleration
  = 0
```

Use midpoint recorded power, grade, weather, and bearing to calculate the observed acceleration/force target consistently with Task 06. Search a fixed finite interval `[0.5, 40] m/s`, require a sign-changing bracket, perform at most 80 bisections, and stop at absolute residual `< 1e-8` or speed width `< 1e-8`.

- [ ] **Step 2: Implement `CalmEquivalentSpeedSolver`**

Keep it deterministic and side-effect free. Return false for every rejected input and numerical failure. The reference calm solve uses `coefficients.AirDensity`; historical wind/density appear in the observed side of the balance, so do not apply them again to the calm candidate.

- [ ] **Step 3: Write failing weather-aware descent tests**

Build at least two activities with enough duration. Assert exact threshold behavior for rain and crosswind, low-wind observed speed acceptance, modest head/tail normalization through the solver, missing-power omission when normalization is needed, fallback when filtering drops below five minutes/two activities, and order independence.

- [ ] **Step 4: Implement the overload and preserve the old path**

For each adjacent, continuous interval in an eligible activity:

1. take midpoint weather/vector and ending gradient/curvature;
2. reject wet or excessive crosswind;
3. when total wind magnitude is below `0.25 m/s`, use ending observed speed;
4. otherwise require the solver and use its calm speed;
5. apply existing descent grid and duration rules.

The `0.25 m/s` calm tolerance is an implementation constant with a named test. Do not route weather-aware evidence through the old overload.

- [ ] **Step 5: Run focused descent and model-domain tests**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~DescentLimitBuilderTests|FullyQualifiedName~CalmEquivalentSpeedSolverTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore --filter FullyQualifiedName~Descent -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: all old and new descent tests pass.

- [ ] **Step 6: Commit and push**

```bash
git add src/RouteTimer.Services/Models tests/RouteTimer.Services.Tests/Models
git commit -m "feat: normalize descent limits for historical weather"
git push
git status --short
```

Expected: successful push and empty status.

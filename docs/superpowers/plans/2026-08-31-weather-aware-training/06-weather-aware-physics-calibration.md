[← Plan overview](README.md)

# Weather-Aware Physics Calibration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recover dry Crr and CdA from historical rides using interval air density and apparent wind.

**Architecture:** Add a weather-resolved calibration overload while retaining the old overload until Task 08 switches orchestration. The robust regression remains unchanged except for its aerodynamic basis and wet-interval filter.

**Tech Stack:** C# numerical code, xUnit synthetic fixtures.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Preserve existing Huber iterations, evidence thresholds, bounds, deterministic sort, and fallback reason codes.
- Exclude an entire interval when midpoint precipitation is `>= wetThreshold`; do not fit a wet Crr.
- Persisted/reference air density remains `PhysicalCoefficients.Default.AirDensity` even though interval density changes the regression basis.

### Task 6: Add the historical-weather calibration overload

**Files:**

- Modify: `src/RouteTimer.Services/Physics/IPhysicsCalibrator.cs`
- Modify: `src/RouteTimer.Services/Physics/PhysicsCalibrator.cs`
- Modify: `tests/RouteTimer.Services.Tests/Physics/PhysicsCalibratorTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/BuildModelJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/ModelValidatorTests.cs`

**Interfaces:**

Keep the old method unchanged for compile-safe incremental delivery and add:

```csharp
PhysicalCalibrationResult Calibrate(
    RiderProfile profile,
    IReadOnlyList<WeatherResolvedActivity> activities,
    double wetThresholdMillimetres);
```

Task 08 removes the old orchestration use, but the overload may remain for targeted calm tests. Update `FakePhysicsCalibrator` implementations in the two named model test files so both overloads record inputs independently.

- [ ] **Step 1: Write a failing known-coefficient weather fixture**

Extend the `PhysicsFixtures` class already inside `PhysicsCalibratorTests.cs`. Generate at least three activities and more than existing minimum evidence. For each interval choose ground speed, grade, acceleration, temperature, pressure, bearing, and wind vector; compute rider power from the same signed force equation with known `.006 Crr` and `.32 CdA`, round to valid `ushort`, and attach matching weather-resolved samples.

Assert recovery within current tolerances and independence from activity reversal.

- [ ] **Step 2: Write failing filter/fallback tests**

Assert:

- changing temperature/pressure while using correct power still recovers coefficients;
- headwind and tailwind intervals both contribute;
- a faster-than-rider tailwind is handled as signed force;
- one wet interval is excluded from exact-minimum evidence;
- just below `0.1 mm` is included and exactly `0.1 mm` excluded;
- invalid/missing weather excludes only that interval;
- insufficient dry evidence returns `insufficient-physics-evidence`;
- old calm overload retains its exact existing results.

- [ ] **Step 3: Refactor observation construction without changing the solver**

Extract common validation/sort/solve code. The weather overload pairs adjacent `WeatherResolvedSample` values from the same activity section, uses midpoint scalar/vector weather, midpoint bearing, and calculates:

```text
response = wheelPower / groundSpeed - gravityForce - mass * acceleration
rollingBasis = mass * g * cos(atan(grade))
aeroBasis = LongitudinalAerodynamicForce(groundSpeed, bearing, wind, rho, cdA: 1)
```

The linear solve still estimates `Crr` and `CdA`. Use the same interval duration/speed/power/grade/acceleration validation as today. Never divide by air-relative speed; the response remains per unit ground distance.

- [ ] **Step 4: Preserve reference snapshot coefficients**

On success return drivetrain efficiency and reference `AirDensity` from `PhysicalCoefficients.Default`, plus fitted Crr/CdA. Do not average historical density into the rider model.

- [ ] **Step 5: Run calibration and full physics tests**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~PhysicsCalibratorTests|FullyQualifiedName~CyclingForces" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: all old and new tests pass.

- [ ] **Step 6: Commit and push**

```bash
git add src/RouteTimer.Services/Physics tests/RouteTimer.Services.Tests/Physics tests/RouteTimer.Services.Tests/Models
git commit -m "feat: calibrate rider physics with historical weather"
git push
git status --short
```

Expected: successful push and empty status.

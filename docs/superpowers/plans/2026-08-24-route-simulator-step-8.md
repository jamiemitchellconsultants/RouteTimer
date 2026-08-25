# Sequential Route Simulator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace equilibrium-speed prediction with an immutable, calibrated rider model and deterministic sequential route simulation with conservative descent limits.

**Architecture:** A shared geometry service enriches both routes and training samples. Separate physics-calibration and descent-limit builders feed an immutable `RiderModel`; the predictor then integrates speed through the route in bounded substeps and returns stable confidence/warning metadata that the durable prediction workflow persists.

**Tech Stack:** .NET 10, C# 14, xUnit, FluentAssertions, EF Core 10, Npgsql/PostgreSQL, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-24-route-simulator-step-8-design.md`

## Global Constraints

- Keep drivetrain efficiency at `0.97` and air density at `1.225 kg/m³`; fit only `Crr` and `CdA`.
- Bound `Crr` to `0.002..0.012` and `CdA` to `0.15..0.60 m²`.
- Use only eligible activities and never cross `CrossesDiscontinuity` when deriving evidence.
- Require physics intervals to be `0 < duration <= 10 s`, speed `3..20 m/s`, power `1..2,000 W`, grade `-2%..+20%`, and absolute acceleration `<= 0.30 m/s²`.
- Require at least 60 physics intervals, 10 minutes, two activities, speed standard deviation `>= 1 m/s`, and grade range `>= 0.02`.
- Use descent grade bands `mild`, `medium`, `steep` and curvature bands `straight`, `moderate`, `tight` exactly as defined in the spec.
- Require five minutes and two activities before learning a descent cell; high confidence requires 20 minutes and three activities.
- Clamp fallback descent caps to the conservative grade/curvature formula; clamp learned caps to `2..20 m/s` and the actual-curvature cap while using the grade cap only as the shrinkage target.
- Start prediction at `0.5 m/s`, carry terminal speed between segments, and accept only substeps of at most one simulated second.
- Emit only stable warning/reason codes; never expose raw residuals, activity contents, stack traces, or rider data.
- Preserve existing upload eligibility, moving-time, persisted sequence, historical prediction, and immutable model-version contracts.
- Use test-first development and run the full solution, formatting, and diff checks before completion.

---

## File Structure

- `src/RouteTimer.Services/Routes/RouteGeometry.cs`: shared distance, robust elevation, gradient, and curvature calculations.
- `src/RouteTimer.Services/Activities/TrainingGeometryEnricher.cs`: section-aware enrichment of cleaned samples.
- `src/RouteTimer.Services/Physics/PhysicsCalibrator.cs`: interval selection and bounded robust `Crr`/`CdA` fitting.
- `src/RouteTimer.Services/Physics/PhysicalCalibrationResult.cs`: immutable calibration output and reason code.
- `src/RouteTimer.Domain/Models/DescentLimit*.cs`: immutable descent grid, bands, cells, and lookup result.
- `src/RouteTimer.Services/Models/DescentLimitBuilder.cs`: learned/shrunk/fallback descent cells.
- `src/RouteTimer.Services/Predictions/DescentSpeedLimiter.cs`: segment cap resolution.
- `src/RouteTimer.Services/Predictions/RoutePredictor.cs`: sequential kinetic-energy integration only.
- `src/RouteTimer.Persistence/Entities/RiderModelDescentLimitEntity.cs`: normalized descent-cell persistence.
- Existing handlers/repositories wire these focused components together without adding UI or configuration surface.

### Task 1: Shared training geometry and discontinuity semantics

**Files:**
- Create: `src/RouteTimer.Services/Routes/RouteGeometry.cs`
- Create: `src/RouteTimer.Services/Activities/TrainingGeometryEnricher.cs`
- Modify: `src/RouteTimer.Domain/Activities/CleanRideSample.cs`
- Modify: `src/RouteTimer.Services/Activities/TrainingCleaner.cs`
- Modify: `src/RouteTimer.Services/Routes/RouteProcessor.cs`
- Test: `tests/RouteTimer.Services.Tests/Activities/TrainingCleanerTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Activities/TrainingGeometryEnricherTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Routes/RouteGeometryTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Routes/RouteProcessorTests.cs`

**Interfaces:**
- Consumes: `GeoMath.DistanceMetres(GeoPoint, GeoPoint)`, `GeoMath.HeadingRadians(GeoPoint, GeoPoint)`, `RouteProcessingOptions.ElevationWindowMetres`.
- Produces: `RouteGeometry.Enrich(IReadOnlyList<GeoPoint> points, IReadOnlyList<double> cumulativeDistances, double elevationWindowMetres) : IReadOnlyList<GeometryValue>` where `GeometryValue(double SmoothedElevationMetres, double Gradient, double CurvaturePerMetre)`.
- Produces: `ITrainingGeometryEnricher.Enrich(CleanedActivity activity) : CleanedActivity`.
- Produces: `CleanRideSample.CurvaturePerMetre : double` after `Gradient`.

- [ ] **Step 1: Write failing discontinuity and geometry tests**

```csharp
[Fact]
public void Clean_marks_first_retained_sample_after_gap_without_dropping_it()
{
    var activity = ActivityFixtures.EligibleRideWithGap(TimeSpan.FromSeconds(11));
    var cleaned = CreateCleaner().Clean(activity);

    cleaned.Samples.Should().ContainSingle(sample => sample.CrossesDiscontinuity);
    cleaned.Samples.Single(sample => sample.CrossesDiscontinuity).Timestamp
        .Should().Be(activity.Samples.First(sample => sample.Timestamp == activity.Samples[0].Timestamp.AddSeconds(11)).Timestamp);
}

[Fact]
public void Enrich_does_not_derive_gradient_or_curvature_across_gap()
{
    var activity = ActivityFixtures.CleanedTwoSectionsWithSharpBoundary();
    var enriched = CreateEnricher().Enrich(activity);

    enriched.Samples.First(sample => sample.CrossesDiscontinuity).Gradient.Should().BeApproximately(0, 1e-12);
    enriched.Samples.First(sample => sample.CrossesDiscontinuity).CurvaturePerMetre.Should().BeApproximately(0, 1e-12);
}

[Fact]
public void Route_and_training_enrichment_share_identical_geometry_values()
{
    var points = RouteFixtures.PointsExactlyTwentyFiveMetresApart();
    var distances = RouteGeometry.CumulativeDistances(points);
    var expected = RouteGeometry.Enrich(points, distances, 100);
    var actual = CreateEnricher().Enrich(ActivityFixtures.CleanedFrom(points)).Samples;

    actual.Select(sample => sample.Gradient).Should().Equal(expected.Select(value => value.Gradient));
    actual.Select(sample => sample.CurvaturePerMetre).Should().Equal(expected.Select(value => value.CurvaturePerMetre));
}
```

- [ ] **Step 2: Run focused tests and verify RED**

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~TrainingCleanerTests|FullyQualifiedName~TrainingGeometryEnricherTests|FullyQualifiedName~RouteGeometryTests|FullyQualifiedName~RouteProcessorTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: FAIL because the new fixture methods/types/properties do not exist and the cleaner currently drops the gap boundary.

- [ ] **Step 3: Implement shared geometry and section-aware enrichment**

```csharp
public readonly record struct GeometryValue(
    double SmoothedElevationMetres,
    double Gradient,
    double CurvaturePerMetre);

public static class RouteGeometry
{
    public static IReadOnlyList<double> CumulativeDistances(IReadOnlyList<GeoPoint> points);

    public static IReadOnlyList<GeometryValue> Enrich(
        IReadOnlyList<GeoPoint> points,
        IReadOnlyList<double> cumulativeDistances,
        double elevationWindowMetres);
}

public interface ITrainingGeometryEnricher
{
    CleanedActivity Enrich(CleanedActivity activity);
}
```

Implement `RouteGeometry.Enrich` with these exact operations: validate equal non-empty counts and finite coordinates/elevations/distances; for each target select points within `targetDistance ± elevationWindowMetres/2`; begin with ordinary least squares and run three deterministic Huber IRLS iterations; use median absolute residual as scale, stop and retain the current line when scale is `<= 1e-9`, and otherwise weight `1` inside `1.345*scale` or `threshold/abs(residual)` outside; evaluate the local line at the target; derive central gradient with one-sided endpoints; derive absolute antimeridian-safe heading change divided by the surrounding run with zero at endpoints. Throw `ArgumentException` for invalid geometry. In `TrainingGeometryEnricher`, split before every sample whose `CrossesDiscontinuity` is true, reset each section's cumulative distance to zero, call `RouteGeometry.Enrich`, and copy values exactly as follows:

```csharp
sample with
{
    Position = sample.Position with { ElevationMetres = geometry.SmoothedElevationMetres },
    Gradient = geometry.Gradient,
    CurvaturePerMetre = geometry.CurvaturePerMetre
};
```

Change the cleaner gap branch to retain and mark the sample:

```csharp
var crossesDiscontinuity = prior is not null
    && sample.Timestamp - prior.Timestamp > TimeSpan.FromSeconds(10);
if (crossesDiscontinuity)
{
    exclusions["gap"]++;
}

movingCandidates.Add((sample, crossesDiscontinuity));
```

Have `RouteProcessor` call `RouteGeometry.CumulativeDistances` and `RouteGeometry.Enrich` after interpolation, removing its duplicate elevation/gradient/curvature methods. Add `CurvaturePerMetre = 0` as the final optional constructor value for existing `CleanRideSample` call sites. Keep `TrainingCleaner(RouteProcessingOptions routeOptions)` source-compatible and finish `Clean` with:

```csharp
var cleaned = new CleanedActivity(activity.Name, samples, elapsed, quality);
return new TrainingGeometryEnricher(routeOptions).Enrich(cleaned);
```

When consuming the `(sample, crossesDiscontinuity)` candidates, do not add the gap to moving elapsed time:

```csharp
if (previousClean is not null && !crossesDiscontinuity)
{
    elapsed += sample.Timestamp - previousClean.Timestamp;
}
```

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit the geometry slice**

```bash
git add src/RouteTimer.Domain/Activities/CleanRideSample.cs src/RouteTimer.Services/Activities src/RouteTimer.Services/Routes tests/RouteTimer.Services.Tests/Activities tests/RouteTimer.Services.Tests/Routes
git commit -m "feat: share route and training geometry"
```

### Task 2: Bounded robust physical calibration

**Files:**
- Create: `src/RouteTimer.Services/Physics/IPhysicsCalibrator.cs`
- Create: `src/RouteTimer.Services/Physics/PhysicalCalibrationResult.cs`
- Create: `src/RouteTimer.Services/Physics/PhysicsCalibrator.cs`
- Modify: `src/RouteTimer.Services/Physics/CyclingForces.cs`
- Test: `tests/RouteTimer.Services.Tests/Physics/PhysicsCalibratorTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Physics/CyclingForcesTests.cs`

**Interfaces:**
- Consumes: enriched `CleanRideSample.Gradient`, `CrossesDiscontinuity`, `SpeedMetresPerSecond`, `PowerWatts`, and timestamps from Task 1.
- Produces: `IPhysicsCalibrator.Calibrate(RiderProfile profile, IReadOnlyList<CleanedActivity> activities) : PhysicalCalibrationResult`.
- Produces: `PhysicalCalibrationResult(PhysicalCoefficients Coefficients, bool WasCalibrated, string ReasonCode)`.
- Produces: `CyclingForces.GravityForce`, `RollingForce`, `AerodynamicForce`, and `RequiredRiderPower` as finite-checked reusable calculations.

- [ ] **Step 1: Write failing synthetic-recovery and fallback tests**

```csharp
[Fact]
public void Calibrate_recovers_known_coefficients_independent_of_activity_order()
{
    var expected = new PhysicalCoefficients(.97, 1.225, .006, .32);
    var activities = PhysicsFixtures.SyntheticActivities(expected, activityCount: 3, minutesEach: 8);

    var forward = new PhysicsCalibrator().Calibrate(PhysicsFixtures.Profile, activities);
    var reverse = new PhysicsCalibrator().Calibrate(PhysicsFixtures.Profile, activities.Reverse().ToArray());

    forward.WasCalibrated.Should().BeTrue();
    forward.ReasonCode.Should().Be("physics-calibrated");
    forward.Coefficients.Crr.Should().BeApproximately(expected.Crr, .0005);
    forward.Coefficients.CdA.Should().BeApproximately(expected.CdA, .02);
    reverse.Should().Be(forward);
}

[Theory]
[InlineData("too-few", "insufficient-physics-evidence")]
[InlineData("single-speed", "ill-conditioned-physics-fit")]
[InlineData("worse-than-default", "physics-fit-not-improved")]
public void Calibrate_returns_stable_default_fallback(string evidence, string reason)
{
    var result = new PhysicsCalibrator().Calibrate(PhysicsFixtures.Profile, PhysicsFixtures.Named(evidence));
    result.Should().Be(new PhysicalCalibrationResult(PhysicalCoefficients.Default, false, reason));
}
```

- [ ] **Step 2: Run focused physics tests and verify RED**

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~PhysicsCalibratorTests|FullyQualifiedName~CyclingForcesTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: FAIL because the calibrator API and reusable force methods do not exist.

- [ ] **Step 3: Implement deterministic bounded Huber fitting**

```csharp
public sealed record PhysicalCalibrationResult(
    PhysicalCoefficients Coefficients,
    bool WasCalibrated,
    string ReasonCode);

public interface IPhysicsCalibrator
{
    PhysicalCalibrationResult Calibrate(
        RiderProfile profile,
        IReadOnlyList<CleanedActivity> activities);
}
```

Create one observation per adjacent interval that passes every Global Constraint. Both endpoint samples must carry power; use total mass from the profile, midpoint speed/gradient/power, acceleration `(v1-v0)/seconds`, and:

```csharp
var wheelPower = midpointPower * PhysicalCoefficients.Default.DrivetrainEfficiency;
var response = wheelPower / midpointSpeed
    - CyclingForces.GravityForce(midpointGrade, totalMass)
    - totalMass * acceleration;
var rollingBasis = totalMass * CyclingForces.GravityMetresPerSecondSquared;
var aeroBasis = .5 * PhysicalCoefficients.Default.AirDensity * midpointSpeed * midpointSpeed;
```

Sort observations by activity name, start timestamp, and end timestamp. After coverage gates, solve the two-column weighted normal equations for `[Crr,CdA]`, clamp after every solve, recompute residuals, and repeat five Huber IRLS iterations. For the symmetric 2x2 normal matrix, calculate both eigenvalues; reject a non-finite matrix, minimum eigenvalue `<= 1e-12`, or `maxEigenvalue/minEigenvalue > 1e8` as `ill-conditioned-physics-fit`. Use median absolute residual as Huber scale, retaining the current solve when scale is `<= 1e-9`; otherwise use threshold `1.345*scale`. Compare final median absolute residual against defaults and require a strict finite improvement of at least `1e-9`, otherwise return `physics-fit-not-improved`. Accepted coefficients retain default drivetrain efficiency/air density. Add direct force tests for uphill/downhill gravity sign, positive rolling/aero resistance, and kinetic/inertial force balance.

- [ ] **Step 4: Run focused physics tests and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit calibration**

```bash
git add src/RouteTimer.Services/Physics tests/RouteTimer.Services.Tests/Physics
git commit -m "feat: calibrate bounded cycling coefficients"
```

### Task 3: Learned and conservative descent limits

**Files:**
- Create: `src/RouteTimer.Domain/Models/DescentGradeBand.cs`
- Create: `src/RouteTimer.Domain/Models/DescentCurvatureBand.cs`
- Create: `src/RouteTimer.Domain/Models/DescentLimitCell.cs`
- Create: `src/RouteTimer.Domain/Models/DescentLimitModel.cs`
- Create: `src/RouteTimer.Domain/Models/DescentLimitEstimate.cs`
- Create: `src/RouteTimer.Services/Models/IDescentLimitBuilder.cs`
- Create: `src/RouteTimer.Services/Models/DescentLimitBuilder.cs`
- Create: `src/RouteTimer.Services/Predictions/IDescentSpeedLimiter.cs`
- Create: `src/RouteTimer.Services/Predictions/DescentSpeedLimiter.cs`
- Test: `tests/RouteTimer.Services.Tests/Models/DescentLimitBuilderTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Predictions/DescentSpeedLimiterTests.cs`

**Interfaces:**
- Consumes: enriched eligible activities from Task 1.
- Produces: `DescentLimitCell(string GradeKey, string CurvatureKey, double SpeedCapMetresPerSecond, TimeSpan Evidence, int ActivityCount, ConfidenceLevel Confidence, bool IsFallback)`.
- Produces: `DescentLimitModel(IReadOnlyList<DescentLimitCell> Cells)` with constructor validation, deterministic ordering, immutable copy, and `WasLearned` derived from `Cells.Any(cell => !cell.IsFallback)`.
- Produces: `IDescentLimitBuilder.Build(IReadOnlyList<CleanedActivity> activities) : DescentLimitModel`.
- Produces: `IDescentSpeedLimiter.Resolve(double gradient, double curvaturePerMetre, DescentLimitModel model) : DescentLimitEstimate` where the estimate contains cap, confidence, and `UsedFallback`.

- [ ] **Step 1: Write failing learned, shrinkage, fallback, and cap tests**

```csharp
[Fact]
public void Build_learns_ninetieth_percentile_after_minimum_coverage()
{
    var activities = DescentFixtures.CellEvidence("medium", "moderate", minutes: 6, activityCount: 2);
    var model = new DescentLimitBuilder().Build(activities);
    var cell = model.Cells.Single(value => value.GradeKey == "medium" && value.CurvatureKey == "moderate");

    cell.IsFallback.Should().BeFalse();
    cell.Confidence.Should().Be(ConfidenceLevel.Medium);
    cell.SpeedCapMetresPerSecond.Should().BeInRange(2, DescentFixtures.ConservativeCap("medium", "moderate"));
}

[Fact]
public void Build_shrinks_medium_coverage_toward_conservative_cap()
{
    var sparse = new DescentLimitBuilder().Build(DescentFixtures.CellEvidence("steep", "straight", 5, 2));
    var rich = new DescentLimitBuilder().Build(DescentFixtures.CellEvidence("steep", "straight", 20, 3));

    sparse.Cells.Single(cell => cell.GradeKey == "steep" && cell.CurvatureKey == "straight").SpeedCapMetresPerSecond
        .Should().BeLessThan(rich.Cells.Single(cell => cell.GradeKey == "steep" && cell.CurvatureKey == "straight").SpeedCapMetresPerSecond);
}

[Theory]
[InlineData(-.03, 0, 13)]
[InlineData(-.06, 0, 16)]
[InlineData(-.10, 0, 18)]
[InlineData(-.10, .02, 10)]
public void Resolve_uses_conservative_grade_and_curvature_caps(double grade, double curvature, double expected)
{
    var result = new DescentSpeedLimiter().Resolve(grade, curvature, DescentLimitModel.Conservative);
    result.Should().Be(new DescentLimitEstimate(expected, ConfidenceLevel.Low, true));
}
```

- [ ] **Step 2: Run focused descent tests and verify RED**

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~DescentLimitBuilderTests|FullyQualifiedName~DescentSpeedLimiterTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: FAIL because descent domain/service types do not exist.

- [ ] **Step 3: Implement the complete nine-cell grid and lookup**

```csharp
public sealed record DescentLimitEstimate(
    double SpeedCapMetresPerSecond,
    ConfidenceLevel Confidence,
    bool UsedFallback);

public interface IDescentLimitBuilder
{
    DescentLimitModel Build(IReadOnlyList<CleanedActivity> activities);
}

public interface IDescentSpeedLimiter
{
    DescentLimitEstimate Resolve(
        double gradient,
        double curvaturePerMetre,
        DescentLimitModel model);
}
```

Implement grade keys with `mild: [-.04,-.02]`, `medium: [-.08,-.04)`, and `steep: < -.08`; resolve the shared `-.04` boundary to mild and `-.08` to medium for one deterministic cell. Implement curvature keys with `straight: [0,.002)`, `moderate: [.002,.01)`, and `tight: [.01,+infinity)`. Count evidence seconds from adjacent valid intervals inside one section, assign interval speed to its ending sample's cell, and track the outer activity index for distinct-activity count. Compute percentile rank `.9*(count-1)` with linear interpolation. For learned cells compute:

```csharp
var durationWeight = Math.Clamp(evidence.TotalSeconds / TimeSpan.FromMinutes(20).TotalSeconds, 0, 1);
var activityWeight = Math.Clamp(activityCount / 3d, 0, 1);
var shrinkage = Math.Min(durationWeight, activityWeight);
var curvatureCap = representativeCurvature > 0 ? Math.Sqrt(2 / representativeCurvature) : 20;
var hardCap = Math.Min(20, curvatureCap);
var learnedCap = conservativeCap + shrinkage * (observedP90 - conservativeCap);
var effectiveCap = Math.Clamp(Math.Min(learnedCap, hardCap), 2, hardCap);
```

Here `conservativeCap` is `min(20, gradeCap, curvature > 0 ? sqrt(2/curvature) : 20)`. Use the median curvature of the cell's evidence as `representativeCurvature` while building its stored cap. The grade cap is a fallback/shrinkage target, not a hard learned cap; sufficient rider evidence may exceed it. Create fallback rows for all other cells using the grade cap additionally limited at the lower boundary of moderate/tight curvature (`.002`/`.01`). On lookup, a learned cell returns `min(storedCellCap, 20, actualCurvature > 0 ? sqrt(2/actualCurvature) : 20)`; a fallback cell additionally applies the actual grade cap. This actual-curvature clamp keeps every point safe. `Resolve` returns no cap for grades above `-.02` by returning `double.PositiveInfinity`, high confidence, and false fallback; the predictor must apply only finite caps on descents.

- [ ] **Step 4: Run focused descent tests and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit descent modelling**

```bash
git add src/RouteTimer.Domain/Models/Descent* src/RouteTimer.Services/Models/IDescentLimitBuilder.cs src/RouteTimer.Services/Models/DescentLimitBuilder.cs src/RouteTimer.Services/Predictions/IDescentSpeedLimiter.cs src/RouteTimer.Services/Predictions/DescentSpeedLimiter.cs tests/RouteTimer.Services.Tests/Models/DescentLimitBuilderTests.cs tests/RouteTimer.Services.Tests/Predictions/DescentSpeedLimiterTests.cs
git commit -m "feat: model conservative descent limits"
```

### Task 4: Immutable rider-model aggregate and normalized persistence

**Files:**
- Create: `src/RouteTimer.Persistence/Entities/RiderModelDescentLimitEntity.cs`
- Create: `src/RouteTimer.Persistence/Migrations/20260824200000_AddSequentialSimulationModel.cs`
- Create: `src/RouteTimer.Persistence/Migrations/20260824200000_AddSequentialSimulationModel.Designer.cs`
- Modify: `src/RouteTimer.Domain/Models/RiderModel.cs`
- Modify: `src/RouteTimer.Domain/Models/RiderModelSnapshot.cs`
- Modify: `src/RouteTimer.Persistence/Entities/ActivitySampleEntity.cs`
- Modify: `src/RouteTimer.Persistence/Entities/RiderModelEntity.cs`
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/TrainingActivityRepository.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/RiderModelRepository.cs`
- Modify: `src/RouteTimer.Persistence/Migrations/RouteTimerDbContextModelSnapshot.cs`
- Modify: `src/RouteTimer.Services/Persistence/IRiderModelRepository.cs`
- Test: `tests/RouteTimer.Persistence.Tests/PostgresMigrationTests.cs`
- Test: `tests/RouteTimer.Persistence.Tests/RepositoryRoundTripTests.cs`

**Interfaces:**
- Consumes: `DescentLimitModel` and `PhysicalCalibrationResult.WasCalibrated` from Tasks 2-3.
- Produces: `RiderModel(PowerModel PowerModel, PhysicalCoefficients Coefficients, DescentLimitModel DescentLimits, bool WasCalibrated, string AlgorithmVersion)`.
- Produces: `RiderModelSnapshot(Guid Id, DateTimeOffset CreatedAt, RiderProfile ProfileSnapshot, RiderModel Model, ModelValidationSummary Validation)` plus derived `WasCalibrated => Model.WasCalibrated` and `DescentWasLearned => Model.DescentLimits.WasLearned`.
- Produces: `IRiderModelRepository.SaveAsync(RiderModel model, RiderProfile profileSnapshot, ModelValidationSummary validation, CancellationToken cancellationToken) : Task<Guid>`.

- [ ] **Step 1: Write failing round-trip and upgrade tests**

```csharp
[Fact]
public async Task Rider_model_round_trips_calibration_and_all_descent_cells()
{
    await using var context = CreateContext();
    var repository = new RiderModelRepository(context);
    var model = ModelFixtures.SequentialModel(wasCalibrated: true, learnedDescent: true);

    var id = await repository.SaveAsync(model, ModelFixtures.Profile, ModelFixtures.Validation, default);
    var loaded = await repository.GetAsync(id, default);

    loaded.Should().NotBeNull();
    loaded!.Model.Should().BeEquivalentTo(model, options => options.WithStrictOrdering());
    loaded.WasCalibrated.Should().BeTrue();
    loaded.DescentWasLearned.Should().BeTrue();
}

[Fact]
public async Task Migration_preserves_existing_model_and_supplies_conservative_descent_grid()
{
    await MigrateToAsync("20260824184955_AddDurablePredictions");
    var oldModelId = await InsertLegacyRiderModelAsync();
    await MigrateToLatestAsync();

    var loaded = await new RiderModelRepository(CreateContext()).GetAsync(oldModelId, default);
    loaded!.Model.DescentLimits.Cells.Should().HaveCount(9);
    loaded.DescentWasLearned.Should().BeFalse();
}

[Fact]
public async Task Training_activity_round_trips_curvature()
{
    var activity = ActivityFixtures.CleanedWithCurvature(.0125);
    await CreateTrainingRepository().SaveAsync(activity, default);
    var loaded = await CreateTrainingRepository().GetAllAsync(default);
    loaded.Single().Samples.Single().CurvaturePerMetre.Should().Be(.0125);
}
```

- [ ] **Step 2: Run focused persistence tests and verify RED**

Run:

```bash
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~PostgresMigrationTests|FullyQualifiedName~RepositoryRoundTripTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: FAIL because the aggregate/repository signatures and schema do not persist descent cells or curvature.

- [ ] **Step 3: Move provenance into the immutable model and update call sites**

```csharp
public sealed record RiderModel(
    PowerModel PowerModel,
    PhysicalCoefficients Coefficients,
    DescentLimitModel DescentLimits,
    bool WasCalibrated,
    string AlgorithmVersion);

public sealed record RiderModelSnapshot(
    Guid Id,
    DateTimeOffset CreatedAt,
    RiderProfile ProfileSnapshot,
    RiderModel Model,
    ModelValidationSummary Validation)
{
    public bool WasCalibrated => Model.WasCalibrated;
    public bool DescentWasLearned => Model.DescentLimits.WasLearned;
}
```

Change every `new RiderModel(...)` in `src` and `tests` to pass `DescentLimitModel.Conservative` and the correct calibration flag. Remove the separate `wasCalibrated` repository parameter so the saved entity always takes provenance from the exact aggregate being persisted.

- [ ] **Step 4: Add normalized persistence mappings and projections**

```csharp
public sealed class RiderModelDescentLimitEntity
{
    public Guid ModelId { get; set; }
    public string GradeKey { get; set; } = "";
    public string CurvatureKey { get; set; } = "";
    public double SpeedCapMetresPerSecond { get; set; }
    public double EvidenceSeconds { get; set; }
    public int ActivityCount { get; set; }
    public string Confidence { get; set; } = "Low";
    public bool IsFallback { get; set; }
    public RiderModelEntity Model { get; set; } = null!;
}
```

Map `(ModelId, GradeKey, CurvatureKey)` as the composite key with cascade delete and a required navigation collection. Add non-null `CurvaturePerMetre` default `0` to activity samples and `DescentWasLearned` default `false` to rider models. Save all nine model cells in the same `SaveChangesAsync`; include `DescentLimits` in both repository queries; parse confidence with `Enum.TryParse` and reject malformed/non-finite/negative persisted values with `InvalidOperationException` before constructing the domain model.

- [ ] **Step 5: Generate one migration, rename it to the planned deterministic name, and define legacy fallback rows**

Run:

```bash
dotnet ef migrations add AddSequentialSimulationModel --project src/RouteTimer.Persistence/RouteTimer.Persistence.csproj --startup-project src/RouteTimer.Api/RouteTimer.Api.csproj
```

Rename the generated `.cs` and `.Designer.cs` files to `20260824200000_AddSequentialSimulationModel.cs` and `20260824200000_AddSequentialSimulationModel.Designer.cs`, and change the designer's `Migration` attribute to `20260824200000_AddSequentialSimulationModel` so file name and EF migration id agree. In `Up`, add the columns/table/index, then insert the nine conservative rows for every existing model with SQL using grade caps `13/16/18`, curvature-band caps `20/sqrt(2/.002)/sqrt(2/.01)`, zero evidence/activity count, `Low`, and `IsFallback=true`. `Down` drops the table and both new columns.

- [ ] **Step 6: Run focused persistence tests and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 7: Commit immutable persistence**

```bash
git add src/RouteTimer.Domain/Models src/RouteTimer.Persistence src/RouteTimer.Services/Persistence tests/RouteTimer.Persistence.Tests tests/RouteTimer.Services.Tests
git commit -m "feat: persist immutable simulation models"
```

### Task 5: Full model construction and validation-fold isolation

**Files:**
- Modify: `src/RouteTimer.Services/Models/BuildModelJobHandler.cs`
- Modify: `src/RouteTimer.Services/Models/ModelValidator.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Test: `tests/RouteTimer.Services.Tests/Models/BuildModelJobHandlerTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Models/ModelValidatorTests.cs`
- Test: `tests/RouteTimer.Api.Tests/HealthEndpointTests.cs`

**Interfaces:**
- Consumes: `ITrainingGeometryEnricher`, `IPhysicsCalibrator`, `IDescentLimitBuilder`, and immutable `RiderModel` from Tasks 1-4.
- Produces: production models tagged `BuildModelJobHandler.AlgorithmVersion == "route-model-v2"`.
- Produces: `ModelValidator(IPowerModelBuilder, IPhysicsCalibrator, IDescentLimitBuilder, IRouteProcessor, IRoutePredictor)` with every fold component built only from `trainingSet`.

- [ ] **Step 1: Write failing orchestration and leakage tests**

```csharp
[Fact]
public async Task Handler_enriches_existing_rows_and_saves_complete_model()
{
    var harness = BuildModelHarness.WithEligibleActivities(3);
    await harness.Handler.HandleAsync(harness.Job, default);

    harness.Geometry.Inputs.Should().HaveCount(3);
    harness.Calibrator.LastActivities.Should().Equal(harness.Geometry.Outputs);
    harness.DescentBuilder.LastActivities.Should().Equal(harness.Geometry.Outputs);
    harness.Models.SavedModel.Should().Be(new RiderModel(
        harness.PowerModel,
        harness.Calibration.Coefficients,
        harness.DescentModel,
        harness.Calibration.WasCalibrated,
        "route-model-v2"));
}

[Fact]
public void Validation_never_sends_held_out_activity_to_any_fold_builder()
{
    var activities = ModelFixtures.DistinctEligibleActivities(4);
    var spy = new FoldInputSpy();
    var validator = CreateValidator(spy);

    validator.Validate(ModelFixtures.Profile, activities);

    spy.PowerInputs.Should().HaveCount(4);
    spy.CalibrationInputs.Should().HaveCount(4);
    spy.DescentInputs.Should().HaveCount(4);
    for (var fold = 0; fold < 4; fold++)
    {
        spy.PowerInputs[fold].Should().NotContainSame(activities[fold]);
        spy.CalibrationInputs[fold].Should().Equal(spy.PowerInputs[fold]);
        spy.DescentInputs[fold].Should().Equal(spy.PowerInputs[fold]);
    }
}
```

- [ ] **Step 2: Run focused handler/validator/API tests and verify RED**

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~BuildModelJobHandlerTests|FullyQualifiedName~ModelValidatorTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~HealthEndpointTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: FAIL because handlers do not yet build calibration/descent data and DI lacks the new services.

- [ ] **Step 3: Compose the full production model**

```csharp
public const string AlgorithmVersion = "route-model-v2";

var enrichedActivities = allActivities
    .Select(geometryEnricher.Enrich)
    .ToArray();
var powerModel = builder.Build(profile, enrichedActivities);
var calibration = calibrator.Calibrate(profile, enrichedActivities);
var descentLimits = descentBuilder.Build(enrichedActivities);
var model = new RiderModel(
    powerModel,
    calibration.Coefficients,
    descentLimits,
    calibration.WasCalibrated,
    AlgorithmVersion);
var validation = validator.Validate(profile, enrichedActivities);
await models.SaveAsync(model, profile, validation, cancellationToken);
```

Retain the existing permanent errors for missing profile/eligible activities/no power evidence. Re-enrich every loaded activity so rows persisted before Task 1 gain real gradient/curvature in memory; do not write those recalculated values back during model build.

- [ ] **Step 4: Build every validation fold from its training subset**

```csharp
var trainingSet = eligible.Where((_, index) => index != heldOutIndex).ToArray();
var foldPower = builder.Build(profile, trainingSet);
var foldCalibration = calibrator.Calibrate(profile, trainingSet);
var foldDescents = descentBuilder.Build(trainingSet);
var foldModel = new RiderModel(
    foldPower,
    foldCalibration.Coefficients,
    foldDescents,
    foldCalibration.WasCalibrated,
    FoldAlgorithmVersion);
```

Keep the existing skip behavior for an unusable held-out route or impossible fold prediction. A calibration/descent fallback is a usable fold, not a reason to skip it.

- [ ] **Step 5: Register all concrete services and verify the application graph**

```csharp
builder.Services.AddSingleton<ITrainingGeometryEnricher>(_ => new TrainingGeometryEnricher(RouteProcessingOptions.Default));
builder.Services.AddSingleton<IPhysicsCalibrator, PhysicsCalibrator>();
builder.Services.AddSingleton<IDescentLimitBuilder, DescentLimitBuilder>();
builder.Services.AddSingleton<IDescentSpeedLimiter, DescentSpeedLimiter>();
```

Update `TrainingCleaner` registration only if Task 1 changed its constructor; keep every stateless service singleton. Update all test fakes to match exact constructor/repository signatures.

- [ ] **Step 6: Run focused handler/validator/API tests and verify GREEN**

Run the Step 2 commands. Expected: PASS.

- [ ] **Step 7: Commit model orchestration**

```bash
git add src/RouteTimer.Services/Models src/RouteTimer.Api/Program.cs tests/RouteTimer.Services.Tests/Models tests/RouteTimer.Api.Tests
git commit -m "feat: build calibrated rider models"
```

### Task 6: Sequential simulation, confidence, and durable warnings

**Files:**
- Modify: `src/RouteTimer.Domain/Predictions/PredictionResult.cs`
- Modify: `src/RouteTimer.Services/Predictions/RoutePredictor.cs`
- Modify: `src/RouteTimer.Services/Predictions/PredictionJobHandler.cs`
- Test: `tests/RouteTimer.Services.Tests/Predictions/RoutePredictorTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Predictions/PredictionWorkflowTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Predictions/PredictionFixtures.cs`

**Interfaces:**
- Consumes: `CyclingForces` from Task 2, `IDescentSpeedLimiter` from Task 3, and complete `RiderModel` from Task 4.
- Produces: `PredictionResult(IReadOnlyList<PredictionSegment> Segments, TimeSpan MovingTime, ConfidenceLevel Confidence, IReadOnlyList<string> Warnings)`.
- Produces: `RoutePredictor(IDescentSpeedLimiter descentLimiter)`.
- Produces internally: `AcceptedSubstep(double ExitSpeedMetresPerSecond, double Seconds)` and `TryAdvance(double entrySpeed, double distance, double grade, double riderPower, double mass, PhysicalCoefficients coefficients) : AcceptedSubstep?`.

- [ ] **Step 1: Replace the minimal predictor test with failing sequential-behavior tests**

```csharp
[Fact]
public void Predict_carries_terminal_speed_and_uses_cumulative_time_for_power_lookup()
{
    var result = PredictionFixtures.PredictTwoLongSegmentsWithDurationBands();

    result.Segments.Should().HaveCount(2);
    result.Segments[1].PowerWatts.Should().NotBe(result.Segments[0].PowerWatts);
    result.Segments[1].SpeedMetresPerSecond.Should().BeGreaterThan(.5);
    result.MovingTime.Should().Be(result.Segments.Aggregate(TimeSpan.Zero, (sum, segment) => sum + segment.MovingTime));
}

[Fact]
public void Predict_applies_curvature_cap_and_emits_one_conservative_warning()
{
    var result = PredictionFixtures.PredictUncoveredCurvedDescent();
    result.Segments.Max(segment => segment.SpeedMetresPerSecond).Should().BeLessThanOrEqualTo(10);
    result.Warnings.Should().Equal("conservative-descent-limits");
    result.Confidence.Should().Be(ConfidenceLevel.Low);
}

[Fact]
public void Predict_is_deterministic_and_every_value_is_finite_non_negative()
{
    var first = PredictionFixtures.PredictMixedRoute();
    var second = PredictionFixtures.PredictMixedRoute();
    second.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
    first.Segments.Should().OnlyContain(segment =>
        double.IsFinite(segment.SpeedMetresPerSecond)
        && segment.SpeedMetresPerSecond >= 0
        && double.IsFinite(segment.MovingTime.TotalSeconds)
        && segment.MovingTime > TimeSpan.Zero);
}

[Theory]
[MemberData(nameof(PredictionFixtures.FinitePropertyCases), MemberType = typeof(PredictionFixtures))]
public void Predict_is_finite_over_supported_inputs(double grade, double watts, double mass, double curvature, double distance)
{
    var result = PredictionFixtures.PredictSingle(grade, watts, mass, curvature, distance);
    result.Segments.Should().OnlyContain(segment => double.IsFinite(segment.SpeedMetresPerSecond) && segment.SpeedMetresPerSecond >= 0);
    result.MovingTime.Should().BeGreaterThan(TimeSpan.Zero);
}
```

- [ ] **Step 2: Add failing confidence, warning-deduplication, and permanent-failure workflow tests**

```csharp
[Theory]
[InlineData(.80, .20, true, ConfidenceLevel.High)]
[InlineData(.79, .21, true, ConfidenceLevel.Medium)]
[InlineData(.80, .20, false, ConfidenceLevel.Low)]
public void Predict_uses_time_weighted_confidence_thresholds(double highShare, double mediumShare, bool calibrated, ConfidenceLevel expected)
{
    PredictionFixtures.PredictWithConfidenceShares(highShare, mediumShare, calibrated).Confidence.Should().Be(expected);
}

[Fact]
public async Task Handler_merges_predictor_and_model_warnings_without_duplicates()
{
    var harness = PredictionWorkflowHarness.WithWarnings(
        predictorWarnings: ["conservative-descent-limits", "uncalibrated-coefficients"],
        calibrated: false,
        validation: ModelValidationStatus.Failed);
    await harness.Handler.HandleAsync(harness.Job, default);

    harness.Published!.Warnings.Should().Equal(
        "conservative-descent-limits",
        "uncalibrated-coefficients",
        "model-validation-failed");
}

[Fact]
public async Task Handler_does_not_publish_partial_segments_when_simulation_is_invalid()
{
    var harness = PredictionWorkflowHarness.WithNonFiniteSimulation();
    var act = () => harness.Handler.HandleAsync(harness.Job, default);
    await act.Should().ThrowAsync<PredictionJobException>().Where(exception => exception.Code == "invalid-prediction-result");
    harness.Published.Should().BeNull();
}
```

- [ ] **Step 3: Run focused predictor/workflow tests and verify RED**

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~RoutePredictorTests|FullyQualifiedName~PredictionWorkflowTests" -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
```

Expected: FAIL because prediction still solves independent equilibrium speeds and has no result warnings.

- [ ] **Step 4: Implement bounded kinetic-energy substeps**

```csharp
private sealed record AcceptedSubstep(double ExitSpeedMetresPerSecond, double Seconds);

private static AcceptedSubstep? TryAdvance(
    double entrySpeed,
    double distance,
    double grade,
    double riderPower,
    double mass,
    PhysicalCoefficients coefficients)
{
    var forceSpeed = Math.Max(entrySpeed, .5);
    var wheelPower = riderPower * coefficients.DrivetrainEfficiency;
    var drivingForce = wheelPower / forceSpeed;
    var resistance = CyclingForces.GravityForce(grade, mass)
        + CyclingForces.RollingForce(grade, mass, coefficients.Crr)
        + CyclingForces.AerodynamicForce(entrySpeed, coefficients.AirDensity, coefficients.CdA);
    var acceleration = (drivingForce - resistance) / mass;
    var exitSquared = entrySpeed * entrySpeed + 2 * acceleration * distance;
    if (!double.IsFinite(exitSquared))
        throw new PredictionCalculationException("Prediction produced non-finite energy.");
    if (exitSquared < 0)
        return null;
    var exitSpeed = Math.Sqrt(exitSquared);
    var seconds = 2 * distance / (entrySpeed + exitSpeed);
    if (!double.IsFinite(seconds) || seconds <= 0)
        throw new PredictionCalculationException("Prediction could not advance along the route.");
    return new AcceptedSubstep(exitSpeed, seconds);
}
```

Before simulation validate positive finite total mass; positive finite segment distances; finite gradients/curvature; non-negative finite lookup power; and finite coefficients with drivetrain efficiency `> 0`, air density `> 0`, `Crr >= 0`, and `CdA >= 0`. For each route sample after the first, call `PowerLookup.GetWatts(sample.Gradient, elapsed)` once. Begin with `remainingDistance = sample.SegmentDistanceMetres` and `proposal = remainingDistance`; call `TryAdvance`; if it returns null, halve the proposal. Otherwise apply `min(exitSpeed, resolvedDescentCap)` when the cap is finite and recompute time as `2*proposal/(entrySpeed+cappedExitSpeed)`; if that final capped time is greater than one second, halve and retry. Accept only when distance/time/speed are finite and positive, subtract distance, add time, and carry capped exit speed. Limit each segment to 100,000 proposal/accept iterations and throw `PredictionCalculationException` on exhaustion or zero progress. Store `sample.SegmentDistanceMetres / segmentSeconds` as segment speed, the selected rider power, and the minimum applicable confidence.

- [ ] **Step 5: Implement route confidence and stable warning aggregation**

```csharp
var physicalConfidence = model.WasCalibrated ? ConfidenceLevel.High : ConfidenceLevel.Low;
var segmentConfidence = Min(estimate.Confidence, physicalConfidence);
if (estimate.Extrapolated) warnings.Add("power-model-extrapolation");
if (descent.UsedFallback) warnings.Add("conservative-descent-limits");

var totalSeconds = segments.Sum(segment => segment.MovingTime.TotalSeconds);
var highShare = segments.Where(segment => segment.Confidence == ConfidenceLevel.High)
    .Sum(segment => segment.MovingTime.TotalSeconds) / totalSeconds;
var mediumOrHighShare = segments.Where(segment => segment.Confidence >= ConfidenceLevel.Medium)
    .Sum(segment => segment.MovingTime.TotalSeconds) / totalSeconds;
var routeConfidence = !model.WasCalibrated ? ConfidenceLevel.Low
    : highShare >= .80 ? ConfidenceLevel.High
    : mediumOrHighShare >= .80 ? ConfidenceLevel.Medium
    : ConfidenceLevel.Low;
```

Use an insertion-ordered `List<string>` plus `HashSet<string>(StringComparer.Ordinal)` helper so warning order is deterministic and duplicates are removed. In `PredictionJobHandler.ApplyModelWarnings`, seed the list/set from `result.Warnings`, then append model warnings in existing order. Read calibration from `model.Model.WasCalibrated` (or the snapshot's derived property). Preserve the existing validation confidence downgrades and durable publication validation.

- [ ] **Step 6: Run focused predictor/workflow tests and verify GREEN**

Run the Step 3 command. Expected: PASS.

- [ ] **Step 7: Run the entire solution and formatting checks**

Run:

```bash
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet format RouteTimer.slnx --no-restore --verify-no-changes --severity error
git diff --check
```

Expected: all discovered tests pass, formatter exits 0, and `git diff --check` emits no output.

- [ ] **Step 8: Commit sequential prediction**

```bash
git add src/RouteTimer.Domain/Predictions/PredictionResult.cs src/RouteTimer.Services/Predictions tests/RouteTimer.Services.Tests/Predictions
git commit -m "feat: simulate routes sequentially"
```

### Task 7: Final acceptance and review gate

**Files:**
- Modify only files required to address review findings.
- Review: `docs/superpowers/specs/2026-08-24-route-simulator-step-8-design.md`
- Review: every file changed since the plan commit.

**Interfaces:**
- Consumes: all deliverables from Tasks 1-6.
- Produces: a clean, review-ready `codex/step-8` branch satisfying every acceptance criterion in the spec.

- [ ] **Step 1: Verify spec coverage from the final diff**

Run:

```bash
git diff --stat main...HEAD
git diff --name-status main...HEAD
git log --oneline main..HEAD
```

Expected: geometry, calibration, descent modelling/persistence, fold isolation, sequential simulation, confidence/warnings, and their tests are all represented; there are no UI, deployment, or unrelated changes.

- [ ] **Step 2: Run the complete verification suite from a clean process**

Run:

```bash
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal
dotnet format RouteTimer.slnx --no-restore --verify-no-changes --severity error
git diff --check
git status --short
```

Expected: all discovered tests pass; formatting/diff checks exit 0; status shows no uncommitted implementation changes.

- [ ] **Step 3: Request code review and address only verified findings**

Use `superpowers:requesting-code-review`. The reviewer must compare the final branch against the step-8 spec, check numerical safeguards and persisted backward compatibility, and classify findings by severity with file/line evidence. For each accepted finding, add a reproducing test first, implement the smallest correction, rerun its focused suite, then rerun Step 2.

- [ ] **Step 4: Commit any review corrections**

If review required changes:

```bash
git add src tests
git commit -m "fix: address step 8 review findings"
```

If no changes were required, do not create an empty commit.

- [ ] **Step 5: Prepare branch integration**

Use `superpowers:finishing-a-development-branch` and present its integration options only after Step 2 remains green after review.

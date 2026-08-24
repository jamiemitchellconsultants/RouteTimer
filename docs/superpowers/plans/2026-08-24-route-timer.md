# RouteTimer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a public, Keycloak-protected RouteTimer application that learns one road cyclist's typical power from FIT activities and produces detailed moving-time predictions for GPX routes.

**Architecture:** A standalone Blazor WebAssembly client and ASP.NET Core API share one public origin. Domain and service projects contain pure analysis rules and use cases; an EF Core/Npgsql persistence project stores retained uploads, samples, jobs, immutable models, and predictions in PostgreSQL. A database-backed worker performs parsing, model building, validation, and prediction while Docker exposes the app only through the existing LocalAI Caddy network.

**Tech Stack:** .NET 10, C#, standalone Blazor WebAssembly, ASP.NET Core 10.0.11, Garmin.FIT.Sdk 21.213.0, EF Core 10.0.11, Npgsql EF provider 10.0.3, PostgreSQL 16+, xUnit, Testcontainers.PostgreSql 4.14.0, bUnit 2.9.0, Playwright, Leaflet, Chart.js, Docker Compose, Caddy 2, Keycloak 26, PowerShell/Pester.

**Spec:** `docs/superpowers/specs/2026-08-24-route-timer-design.md`

## Global Constraints

- Target `net10.0` and use the repository's installed .NET 10 SDK; do not introduce preview packages.
- Training input is FIT and must have at least 10 minutes moving time, 95% GPS/elevation/speed coverage, and 80% power coverage.
- Prediction input is elevation-bearing GPX; XML DTD and external entities are disabled.
- The model predicts a typical road ride in calm, dry conditions and moving time only.
- Rider weight and bike/equipment weight are both required; persisted values and API units are kilograms, metres, seconds, metres/second, and watts.
- Raw uploads, parsed samples, model versions, and prediction snapshots are retained in PostgreSQL.
- Personal sample files under `/Users/jamesmitchell/RiderProjects/RouteTimer` are exploratory inputs only and must never be copied into or committed to this repository.
- All API routes except `/health/live` and `/health/ready` require a valid `rider` role and `routetimer-api` audience.
- The production app and database publish no host ports; the web container joins the external `mcp-public` network and Caddy is the only public ingress.
- Use test-driven development: add one focused failing test, observe the intended failure, add minimal production code, and rerun the focused test before broader tests.
- Every task ends with `dotnet test RouteTimer.slnx --no-restore` unless the task states a stronger command.

---

## File and Boundary Map

Create this solution layout before feature work expands it:

```text
RouteTimer.slnx
global.json
Directory.Build.props
Directory.Packages.props
src/
  RouteTimer.Client/          standalone WASM UI and JavaScript interop
  RouteTimer.Contracts/       DTOs and stable API error/status codes
  RouteTimer.Domain/          units, entities, model snapshots, physics values
  RouteTimer.Services/        parsers, route analysis, modelling, use cases, ports
  RouteTimer.Persistence/     EF mappings, migrations, repositories, job leasing
  RouteTimer.Api/             auth, endpoints, worker host, health, static client
tests/
  RouteTimer.Domain.Tests/
  RouteTimer.Services.Tests/
  RouteTimer.Persistence.Tests/
  RouteTimer.Api.Tests/
  RouteTimer.Client.Tests/
  RouteTimer.EndToEnd.Tests/
deploy/
  caddy/
  keycloak/
  tests/
docs/
  superpowers/specs/
  superpowers/plans/
```

The core interfaces that later tasks consume are fixed here:

```csharp
public interface IFitActivityParser
{
    Task<ParsedFitActivity> ParseAsync(Stream input, CancellationToken cancellationToken);
}

public interface IGpxRouteParser
{
    Task<ParsedGpxRoute> ParseAsync(Stream input, CancellationToken cancellationToken);
}

public interface IRouteProcessor
{
    ProcessedRoute Process(IReadOnlyList<GeoPoint> points);
}

public interface ITrainingCleaner
{
    CleanedActivity Clean(ParsedFitActivity activity);
}

public interface IPowerModelBuilder
{
    PowerModel Build(RiderProfile profile, IReadOnlyList<CleanedActivity> activities);
}

public interface IRoutePredictor
{
    PredictionResult Predict(ProcessedRoute route, RiderProfile profile, RiderModel model);
}
```

Persistence ports live in `RouteTimer.Services` so `RouteTimer.Persistence` implements inward-facing contracts without reversing dependencies.

---

### Task 1: Solution Shell and Health Contract

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `RouteTimer.slnx`
- Create: `src/RouteTimer.{Client,Contracts,Domain,Services,Persistence,Api}/*`
- Create: `tests/RouteTimer.{Domain,Services,Persistence,Api,Client,EndToEnd}.Tests/*`
- Create: `src/RouteTimer.Api/Program.cs`
- Create: `tests/RouteTimer.Api.Tests/HealthEndpointTests.cs`
- Create: `.gitignore`

**Interfaces:**
- Consumes: none.
- Produces: buildable project graph, `public partial class Program`, anonymous `/health/live`, and authenticated-by-default API policy registration point.

- [ ] **Step 1: Scaffold projects and lock project references**

Run:

```bash
dotnet new sln -n RouteTimer --format slnx
dotnet new blazorwasm -n RouteTimer.Client -o src/RouteTimer.Client
dotnet new classlib -n RouteTimer.Contracts -o src/RouteTimer.Contracts
dotnet new classlib -n RouteTimer.Domain -o src/RouteTimer.Domain
dotnet new classlib -n RouteTimer.Services -o src/RouteTimer.Services
dotnet new classlib -n RouteTimer.Persistence -o src/RouteTimer.Persistence
dotnet new web -n RouteTimer.Api -o src/RouteTimer.Api
dotnet new xunit -n RouteTimer.Domain.Tests -o tests/RouteTimer.Domain.Tests
dotnet new xunit -n RouteTimer.Services.Tests -o tests/RouteTimer.Services.Tests
dotnet new xunit -n RouteTimer.Persistence.Tests -o tests/RouteTimer.Persistence.Tests
dotnet new xunit -n RouteTimer.Api.Tests -o tests/RouteTimer.Api.Tests
dotnet new xunit -n RouteTimer.Client.Tests -o tests/RouteTimer.Client.Tests
dotnet new xunit -n RouteTimer.EndToEnd.Tests -o tests/RouteTimer.EndToEnd.Tests
dotnet sln RouteTimer.slnx add src/RouteTimer.Client/RouteTimer.Client.csproj src/RouteTimer.Contracts/RouteTimer.Contracts.csproj src/RouteTimer.Domain/RouteTimer.Domain.csproj src/RouteTimer.Services/RouteTimer.Services.csproj src/RouteTimer.Persistence/RouteTimer.Persistence.csproj src/RouteTimer.Api/RouteTimer.Api.csproj tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj tests/RouteTimer.EndToEnd.Tests/RouteTimer.EndToEnd.Tests.csproj
dotnet add src/RouteTimer.Client/RouteTimer.Client.csproj reference src/RouteTimer.Contracts/RouteTimer.Contracts.csproj
dotnet add src/RouteTimer.Services/RouteTimer.Services.csproj reference src/RouteTimer.Domain/RouteTimer.Domain.csproj
dotnet add src/RouteTimer.Persistence/RouteTimer.Persistence.csproj reference src/RouteTimer.Services/RouteTimer.Services.csproj src/RouteTimer.Domain/RouteTimer.Domain.csproj
dotnet add src/RouteTimer.Api/RouteTimer.Api.csproj reference src/RouteTimer.Contracts/RouteTimer.Contracts.csproj src/RouteTimer.Services/RouteTimer.Services.csproj src/RouteTimer.Persistence/RouteTimer.Persistence.csproj
dotnet add tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj reference src/RouteTimer.Domain/RouteTimer.Domain.csproj
dotnet add tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj reference src/RouteTimer.Services/RouteTimer.Services.csproj
dotnet add tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj reference src/RouteTimer.Persistence/RouteTimer.Persistence.csproj
dotnet add tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj reference src/RouteTimer.Api/RouteTimer.Api.csproj src/RouteTimer.Client/RouteTimer.Client.csproj
dotnet add tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj reference src/RouteTimer.Client/RouteTimer.Client.csproj
dotnet add tests/RouteTimer.EndToEnd.Tests/RouteTimer.EndToEnd.Tests.csproj reference src/RouteTimer.Contracts/RouteTimer.Contracts.csproj
```

Set `global.json` to SDK `10.0.302` with `rollForward: latestPatch`. Add this repository-wide build configuration:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

Enable central package management in `Directory.Packages.props`, move the exact non-preview versions generated by the .NET 10 templates out of project files into that file, and pin `Microsoft.AspNetCore.Mvc.Testing` 10.0.11 for `Api.Tests`. The final dependency graph must be:

```text
Client -> Contracts
Services -> Domain
Persistence -> Services + Domain
Api -> Contracts + Services + Persistence
tests -> their named production project; Api.Tests also -> Client
```

- [ ] **Step 2: Write the failing health test**

```csharp
[Fact]
public async Task Live_health_is_anonymous_and_returns_healthy()
{
    await using var app = new WebApplicationFactory<Program>();
    using var client = app.CreateClient();
    using var response = await client.GetAsync("/health/live");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
}
```

- [ ] **Step 3: Verify the test fails for the missing host contract**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --filter Live_health_is_anonymous_and_returns_healthy`

Expected: FAIL because `Program` or `/health/live` is missing.

- [ ] **Step 4: Implement the minimal API host**

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    ResponseWriter = static (context, _) => context.Response.WriteAsync("Healthy")
}).AllowAnonymous();
app.Run();
public partial class Program;
```

- [ ] **Step 5: Verify the solution and commit**

Run: `dotnet restore RouteTimer.slnx`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: all tests PASS.

```bash
git add .gitignore global.json Directory.Build.props Directory.Packages.props RouteTimer.slnx src tests
git commit -m "build: scaffold RouteTimer solution"
```

### Task 2: Route Geometry, Resampling, Gradient, and Curvature

**Files:**
- Create: `src/RouteTimer.Domain/Routes/GeoPoint.cs`
- Create: `src/RouteTimer.Domain/Routes/RouteSample.cs`
- Create: `src/RouteTimer.Domain/Routes/ProcessedRoute.cs`
- Create: `src/RouteTimer.Services/Routes/RouteProcessingOptions.cs`
- Create: `src/RouteTimer.Services/Routes/GeoMath.cs`
- Create: `src/RouteTimer.Services/Routes/RouteProcessor.cs`
- Create: `tests/RouteTimer.Services.Tests/Routes/RouteFixtures.cs`
- Test: `tests/RouteTimer.Services.Tests/Routes/RouteProcessorTests.cs`

**Interfaces:**
- Consumes: project graph from Task 1.
- Produces: `GeoPoint`, `RouteSample`, `ProcessedRoute`, and `IRouteProcessor.Process(IReadOnlyList<GeoPoint>)`.

- [ ] **Step 1: Write failing geometry tests**

```csharp
[Fact]
public void Process_resamples_at_25m_and_uses_smoothed_elevation_for_grade()
{
    var points = RouteFixtures.StraightClimb(lengthMetres: 200, riseMetres: 10, noiseMetres: 2);
    var route = new RouteProcessor(RouteProcessingOptions.Default).Process(points);
    Assert.InRange(route.Samples.Count, 8, 10);
    Assert.All(route.Samples.Skip(2).Take(4), p => Assert.InRange(p.Gradient, .04, .06));
}

[Fact]
public void Process_rejects_a_route_with_fewer_than_two_distinct_points() =>
    Assert.Throws<RouteInputException>(() => new RouteProcessor(RouteProcessingOptions.Default)
        .Process([new GeoPoint(51, -2, 50)]));
```

- [ ] **Step 2: Run the focused tests and observe missing types**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter RouteProcessorTests`

Expected: FAIL to compile because route-processing types do not exist.

- [ ] **Step 3: Implement deterministic route processing**

Use these public records and defaults:

```csharp
public readonly record struct GeoPoint(double Latitude, double Longitude, double ElevationMetres);
public sealed record RouteSample(int Sequence, GeoPoint Point, double CumulativeDistanceMetres,
    double SegmentDistanceMetres, double Gradient, double CurvaturePerMetre);
public sealed record ProcessedRoute(IReadOnlyList<RouteSample> Samples, double DistanceMetres, double AscentMetres);
public sealed record RouteProcessingOptions(double SegmentMetres, double ElevationWindowMetres,
    double MinModelGrade, double MaxModelGrade)
{
    public static RouteProcessingOptions Default { get; } = new(25, 100, -.20, .20);
}
```

Implement Haversine distance, antimeridian-safe heading changes, distance interpolation, a robust local linear elevation fit over 100 metres, gradient from fitted elevation, curvature from heading change/distance, ascent from positive smoothed changes, and finite-value guards.

- [ ] **Step 4: Run focused and full tests**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter RouteProcessorTests`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RouteTimer.Domain/Routes src/RouteTimer.Services/Routes tests/RouteTimer.Services.Tests/Routes
git commit -m "feat: process cycling route geometry"
```

### Task 3: Secure GPX Prediction-Route Parser

**Files:**
- Create: `src/RouteTimer.Services/Routes/IGpxRouteParser.cs`
- Create: `src/RouteTimer.Services/Routes/ParsedGpxRoute.cs`
- Create: `src/RouteTimer.Services/Routes/GpxRouteParser.cs`
- Create: `src/RouteTimer.Services/Validation/RouteInputException.cs`
- Create: `tests/RouteTimer.Services.Tests/Routes/GpxFixtures.cs`
- Test: `tests/RouteTimer.Services.Tests/Routes/GpxRouteParserTests.cs`

**Interfaces:**
- Consumes: `GeoPoint` and `IRouteProcessor` from Task 2.
- Produces: `IGpxRouteParser.ParseAsync(Stream, CancellationToken)` returning a named route and ordered elevation-bearing points.

- [ ] **Step 1: Write parser and XML-safety tests**

```csharp
[Fact]
public async Task Parse_accepts_gpx_without_timestamps()
{
    await using var input = GpxFixtures.Route((51.0, -2.0, 50), (51.001, -2.0, 55));
    var route = await new GpxRouteParser().ParseAsync(input, CancellationToken.None);
    Assert.Equal(2, route.Points.Count);
    Assert.Equal(55, route.Points[1].ElevationMetres);
}

[Fact]
public async Task Parse_rejects_doctype()
{
    await using var input = GpxFixtures.WithDoctype();
    await Assert.ThrowsAsync<RouteInputException>(() =>
        new GpxRouteParser().ParseAsync(input, CancellationToken.None));
}
```

- [ ] **Step 2: Verify both tests fail**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter GpxRouteParserTests`

Expected: FAIL because `GpxRouteParser` is missing.

- [ ] **Step 3: Implement a bounded streaming parser**

Use `XmlReader` with `DtdProcessing.Prohibit`, `XmlResolver = null`, async enabled, a counting stream capped at 50 MB, and an explicit 250,000 `trkpt` limit. Accept GPX 1.0/1.1 by local element name, require finite `lat`, `lon`, and child `ele`, preserve metadata/track name, reject fewer than two points, and never fetch schemas or external resources.

```csharp
public sealed record ParsedGpxRoute(string Name, IReadOnlyList<GeoPoint> Points);
```

- [ ] **Step 4: Run focused and full tests**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter GpxRouteParserTests`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS, including oversized-point and missing-elevation cases.

- [ ] **Step 5: Commit**

```bash
git add src/RouteTimer.Services/Routes src/RouteTimer.Services/Validation tests/RouteTimer.Services.Tests/Routes
git commit -m "feat: parse prediction GPX safely"
```

### Task 4: Garmin FIT Activity Adapter

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/RouteTimer.Services/RouteTimer.Services.csproj`
- Create: `src/RouteTimer.Services/Activities/IFitActivityParser.cs`
- Create: `src/RouteTimer.Services/Activities/ParsedFitActivity.cs`
- Create: `src/RouteTimer.Services/Activities/RawRideSample.cs`
- Create: `src/RouteTimer.Services/Activities/ActivitySport.cs`
- Create: `src/RouteTimer.Services/Activities/FitActivityParser.cs`
- Create: `src/RouteTimer.Services/Validation/ActivityInputException.cs`
- Create: `tests/RouteTimer.Services.Tests/Activities/FitTestFileBuilder.cs`
- Test: `tests/RouteTimer.Services.Tests/Activities/FitActivityParserTests.cs`

**Interfaces:**
- Consumes: `GeoPoint` from Task 2 and `Garmin.FIT.Sdk` 21.213.0.
- Produces: `IFitActivityParser`, `ParsedFitActivity`, and ordered `RawRideSample` records with timer state and nullable sensor fields.

- [ ] **Step 1: Add the official SDK and write a synthetic FIT test**

Pin `<PackageVersion Include="Garmin.FIT.Sdk" Version="21.213.0" />` centrally. Build the fixture in memory with Garmin `Encode`, `FileIdMesg`, `EventMesg`, `RecordMesg`, and `SessionMesg`; do not add a personal binary fixture.

```csharp
[Fact]
public async Task Parse_reads_power_position_speed_and_timer_state()
{
    await using var fit = FitTestFileBuilder.ActivityWithPause();
    var result = await new FitActivityParser().ParseAsync(fit, CancellationToken.None);
    Assert.Equal(ActivitySport.Cycling, result.Sport);
    Assert.Contains(result.Samples, s => s.PowerWatts == 220 && s.TimerRunning);
    Assert.Contains(result.Samples, s => !s.TimerRunning);
}
```

- [ ] **Step 2: Verify the adapter test fails**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter FitActivityParserTests`

Expected: FAIL because FIT parser types are missing.

- [ ] **Step 3: Implement the Garmin event adapter**

Use `Decode`, `MesgBroadcaster`, `RecordMesgEvent`, `EventMesgEvent`, and `SessionMesgEvent`. Call SDK integrity validation before reading. Convert semicircle coordinates to degrees, Garmin timestamps to `DateTimeOffset`, enhanced altitude/speed before legacy fields, preserve missing power as null, and track timer start/stop events.

```csharp
public sealed record RawRideSample(DateTimeOffset Timestamp, GeoPoint? Position,
    double? SpeedMetresPerSecond, ushort? PowerWatts, byte? HeartRate, byte? Cadence,
    bool TimerRunning);
public sealed record ParsedFitActivity(string Name, ActivitySport Sport, DateTimeOffset StartedAt,
    IReadOnlyList<RawRideSample> Samples, TimeSpan? DeviceTimerTime, double? DeviceDistanceMetres);
```

Reject non-activity FIT files, integrity failures, missing record messages, non-cycling sessions, and invalid timestamps with stable `ActivityInputException.Code` values.

- [ ] **Step 4: Verify decoding and the solution**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter FitActivityParserTests`

Run an uncommitted exploratory decode against `/Users/jamesmitchell/RiderProjects/RouteTimer/24049918296_ACTIVITY.fit` and print only record counts and field coverage; do not copy or snapshot values.

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: synthetic tests PASS and the personal sample reports non-zero power coverage.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/RouteTimer.Services tests/RouteTimer.Services.Tests/Activities
git commit -m "feat: decode Garmin FIT activities"
```

### Task 5: Moving-Time Cleaning and Training Eligibility

**Files:**
- Create: `src/RouteTimer.Domain/Activities/CleanRideSample.cs`
- Create: `src/RouteTimer.Domain/Activities/CleanedActivity.cs`
- Create: `src/RouteTimer.Domain/Activities/ActivityQuality.cs`
- Create: `src/RouteTimer.Services/Activities/ITrainingCleaner.cs`
- Create: `src/RouteTimer.Services/Activities/TrainingCleaner.cs`
- Create: `tests/RouteTimer.Services.Tests/Activities/ActivityFixtures.cs`
- Test: `tests/RouteTimer.Services.Tests/Activities/TrainingCleanerTests.cs`

**Interfaces:**
- Consumes: raw FIT samples from Task 4 and route calculations from Task 2.
- Produces: eligible/ineligible `CleanedActivity`, 25-metre clean samples, moving duration, coverage percentages, exclusion counts, and stable reason codes.

- [ ] **Step 1: Write tests for pauses, gaps, coverage, and zero power**

```csharp
[Fact]
public void Clean_excludes_pauses_and_gaps_but_keeps_recorded_zero_power()
{
    var parsed = ActivityFixtures.WithPauseGapAndCoasting();
    var cleaned = new TrainingCleaner(RouteProcessingOptions.Default).Clean(parsed);
    Assert.DoesNotContain(cleaned.Samples, s => s.CrossesDiscontinuity);
    Assert.Contains(cleaned.Samples, s => s.PowerWatts == 0);
    Assert.Equal(ActivityEligibility.Eligible, cleaned.Quality.Eligibility);
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter TrainingCleanerTests`

Expected: FAIL because cleaner types are missing.

- [ ] **Step 3: Implement explicit cleaning rules**

Deduplicate timestamps, prefer FIT timer state, use speed >=1.0 m/s only when events are absent, break sections at gaps over 10 seconds, exclude missing/non-finite fields, preserve zero watts, resample continuous sections, derive moving duration cumulatively, and calculate required-field coverage over moving time. Eligibility requires 10 minutes, 95% GPS/elevation/speed, and 80% power. Store exclusion counts keyed by `paused`, `gap`, `missing-position`, `missing-elevation`, `missing-speed`, `missing-power`, and `implausible`.

- [ ] **Step 4: Run focused and full tests**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter TrainingCleanerTests`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RouteTimer.Domain/Activities src/RouteTimer.Services/Activities tests/RouteTimer.Services.Tests/Activities
git commit -m "feat: clean FIT training evidence"
```

### Task 6: Typical-Power Bands and Coverage Confidence

**Files:**
- Create: `src/RouteTimer.Domain/Models/PowerBand.cs`
- Create: `src/RouteTimer.Domain/Models/PowerModel.cs`
- Create: `src/RouteTimer.Domain/Models/ConfidenceLevel.cs`
- Create: `src/RouteTimer.Domain/Profile/RiderProfile.cs`
- Create: `src/RouteTimer.Services/Models/IPowerModelBuilder.cs`
- Create: `src/RouteTimer.Services/Models/PowerModelBuilder.cs`
- Create: `src/RouteTimer.Services/Models/PowerLookup.cs`
- Create: `src/RouteTimer.Services/Models/ConfidenceCalculator.cs`
- Create: `tests/RouteTimer.Services.Tests/Models/ModelFixtures.cs`
- Test: `tests/RouteTimer.Services.Tests/Models/PowerModelBuilderTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Models/PowerLookupTests.cs`

**Interfaces:**
- Consumes: eligible `CleanedActivity` values from Task 5.
- Produces: immutable `PowerModel`, `PowerLookup.GetWatts(double gradient, TimeSpan elapsed)`, and route-weighted high/medium/low coverage reasons.

- [ ] **Step 1: Write failing median, shrinkage, and interpolation tests**

```csharp
[Fact]
public void Build_uses_robust_median_and_distinct_activity_coverage()
{
    var activities = ModelFixtures.ThreeActivities(flatWatts: [180, 200, 1000]);
    var model = new PowerModelBuilder().Build(new RiderProfile(75, 10), activities);
    var flatEarly = model.Bands.Single(b => b.GradeKey == "-1:1" && b.DurationKey == "0:30");
    Assert.InRange(flatEarly.TypicalWatts, 180, 220);
    Assert.Equal(ConfidenceLevel.High, flatEarly.Confidence);
}
```

- [ ] **Step 2: Verify model tests fail**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter "PowerModelBuilderTests|PowerLookupTests"`

Expected: FAIL because model types are missing.

- [ ] **Step 3: Implement bands, robust medians, shrinkage, and lookup**

Use the eight gradient bands and five elapsed-duration bands from the spec. Weight evidence by represented moving seconds, cap each activity's contribution to a cell at the cell median activity duration, and shrink sparse cells toward gradient, duration, then global medians. High requires 15 minutes from three activities; medium requires 5 minutes from two; lower evidence is low. Bilinearly interpolate adjacent band centres and mark nearest-band extrapolation.

```csharp
public sealed record PowerBand(string GradeKey, string DurationKey, double TypicalWatts,
    TimeSpan Evidence, int ActivityCount, double ShrinkageWeight, ConfidenceLevel Confidence);
public sealed record PowerEstimate(double Watts, ConfidenceLevel Confidence, bool Extrapolated, string Reason);
```

- [ ] **Step 4: Run model invariants and full suite**

Add property-style theories asserting finite/non-negative watts, monotonic interpolation bounds, and order independence of activity input.

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter "PowerModelBuilderTests|PowerLookupTests"`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RouteTimer.Domain/Models src/RouteTimer.Domain/Profile src/RouteTimer.Services/Models tests/RouteTimer.Services.Tests/Models
git commit -m "feat: learn typical rider power"
```

### Task 7: Physical Calibration and Sequential Route Simulation

**Files:**
- Create: `src/RouteTimer.Domain/Physics/PhysicalCoefficients.cs`
- Create: `src/RouteTimer.Domain/Models/RiderModel.cs`
- Create: `src/RouteTimer.Domain/Predictions/PredictionResult.cs`
- Create: `src/RouteTimer.Domain/Predictions/PredictionSegment.cs`
- Create: `src/RouteTimer.Services/Physics/CyclingForces.cs`
- Create: `src/RouteTimer.Services/Physics/PhysicsCalibrator.cs`
- Create: `src/RouteTimer.Services/Predictions/IRoutePredictor.cs`
- Create: `src/RouteTimer.Services/Predictions/RoutePredictor.cs`
- Create: `src/RouteTimer.Services/Predictions/DescentSpeedLimiter.cs`
- Create: `tests/RouteTimer.Services.Tests/Predictions/PredictionFixtures.cs`
- Test: `tests/RouteTimer.Services.Tests/Physics/CyclingForcesTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Predictions/RoutePredictorTests.cs`

**Interfaces:**
- Consumes: processed routes from Task 2, `RiderProfile` and `PowerModel` from Task 6.
- Produces: calibrated/default coefficients and deterministic per-segment `PredictionResult`.

- [ ] **Step 1: Write force-balance and simulation tests**

```csharp
[Theory]
[InlineData(0.00, 10.0, 75, 10, 245, 8)]
[InlineData(0.05, 5.0, 75, 10, 261, 12)]
public void Required_power_matches_known_tolerance(double grade, double speed, double riderKg,
    double bikeKg, double expectedWatts, double tolerance) =>
    Assert.InRange(CyclingForces.RequiredRiderPower(grade, speed, riderKg + bikeKg,
        PhysicalCoefficients.Default), expectedWatts - tolerance, expectedWatts + tolerance);

[Fact]
public void Predict_returns_finite_non_negative_segments_and_total_time()
{
    var result = PredictionFixtures.PredictStraightRoute();
    Assert.All(result.Segments, s => Assert.True(double.IsFinite(s.SpeedMetresPerSecond) && s.SpeedMetresPerSecond >= 0));
    Assert.True(result.MovingTime > TimeSpan.Zero);
}
```

- [ ] **Step 2: Verify physics tests fail**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter "CyclingForcesTests|RoutePredictorTests"`

Expected: FAIL because physics types are missing.

- [ ] **Step 3: Implement physical coefficients and bounded calibration**

Use defaults `DrivetrainEfficiency=.97`, `AirDensity=1.225`, `Crr=.005`, `CdA=.32`. Implement gravity, rolling, aero, and kinetic-energy terms in SI units. Fit CdA within `.15..60` and Crr within `.002..012` by robust bounded least squares over steady training samples; reject ill-conditioned fits and return defaults with `WasCalibrated=false` and a reason.

- [ ] **Step 4: Implement sequential prediction**

Create `RiderModel` as the immutable aggregate of `PowerModel`, `PhysicalCoefficients`, descent limits, algorithm version, coverage, and validation metrics. At each 25-metre route segment, obtain power from `PowerLookup`, solve net force, advance kinetic energy using substeps no longer than one simulated second, and accumulate time. Apply a descent cap learned from grade/curvature cells; without evidence use a conservative lateral-acceleration cap and 20 m/s absolute cap. Return segment confidence and route confidence using Task 6's 80%-of-predicted-time rules. Throw `PredictionCalculationException` on non-finite state or failure to advance.

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter "CyclingForcesTests|RoutePredictorTests"`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS.

```bash
git add src/RouteTimer.Domain/Physics src/RouteTimer.Domain/Predictions src/RouteTimer.Services/Physics src/RouteTimer.Services/Predictions tests/RouteTimer.Services.Tests/Physics tests/RouteTimer.Services.Tests/Predictions
git commit -m "feat: simulate cycling route speed"
```

### Task 8: PostgreSQL Schema, Migrations, and Repository Ports

**Files:**
- Modify: `Directory.Packages.props`
- Create: `src/RouteTimer.Services/Persistence/IProfileRepository.cs`
- Create: `src/RouteTimer.Services/Persistence/ITrainingRepository.cs`
- Create: `src/RouteTimer.Services/Persistence/IModelRepository.cs`
- Create: `src/RouteTimer.Services/Persistence/IPredictionRepository.cs`
- Create: `src/RouteTimer.Services/Persistence/IStoredUploadRepository.cs`
- Create: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Create: `src/RouteTimer.Persistence/Entities/*.cs`
- Create: `src/RouteTimer.Persistence/Configurations/*.cs`
- Create: `src/RouteTimer.Persistence/Repositories/*.cs`
- Create: `src/RouteTimer.Persistence/Migrations/*_InitialCreate.cs`
- Test: `tests/RouteTimer.Persistence.Tests/PostgresFixture.cs`
- Create: `tests/RouteTimer.Persistence.Tests/PersistenceFixtures.cs`
- Test: `tests/RouteTimer.Persistence.Tests/RepositoryRoundTripTests.cs`

**Interfaces:**
- Consumes: domain records from Tasks 5–7.
- Produces: transactional storage ports and the initial PostgreSQL schema for profile, uploads, activities/samples, jobs, models/bands, predictions/segments.

- [ ] **Step 1: Add EF/Npgsql/Testcontainers packages and failing round-trip test**

Pin `Microsoft.EntityFrameworkCore.Design` 10.0.11, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, and `Testcontainers.PostgreSql` 4.14.0 in `Directory.Packages.props`. Pin `Microsoft.AspNetCore.Mvc.Testing` 10.0.11 for API tests.

```csharp
[Fact]
public async Task Save_prediction_preserves_model_and_profile_snapshot()
{
    await fixture.ResetAsync();
    var saved = await fixture.Store.SavePredictionAsync(PersistenceFixtures.Prediction(), CancellationToken.None);
    var loaded = await fixture.Store.GetPredictionAsync(saved.Id, CancellationToken.None);
    Assert.Equal(saved.ModelVersion, loaded!.ModelVersion);
    Assert.Equal(saved.RiderWeightKg, loaded.RiderWeightKg);
    Assert.Equal(saved.Segments.Count, loaded.Segments.Count);
}
```

- [ ] **Step 2: Run the PostgreSQL test and observe failure**

Run: `dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --filter RepositoryRoundTripTests`

Expected: FAIL because the context/schema/repositories are missing.

- [ ] **Step 3: Implement mappings and indexes**

Map byte arrays to `bytea`, hashes to fixed 32-byte values with a unique `(Kind,Sha256)` index, UTC timestamps to `timestamptz`, model metrics/warnings to `jsonb`, samples/segments with composite `(ParentId,Sequence)` keys, cascade owned samples/segments, and restrict model deletion while predictions reference it. Add indexes for job state/lease, activity start time, model creation time, and prediction creation time.

- [ ] **Step 4: Generate and verify the migration**

Run: `dotnet ef migrations add InitialCreate --project src/RouteTimer.Persistence --startup-project src/RouteTimer.Api`

Run: `dotnet ef database update --project src/RouteTimer.Persistence --startup-project src/RouteTimer.Api --connection "$ROUTETIMER_TEST_CONNECTION"`

Run: `dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj`

Expected: migration applies to a clean PostgreSQL container and repository tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/RouteTimer.Services/Persistence src/RouteTimer.Persistence tests/RouteTimer.Persistence.Tests
git commit -m "feat: persist RouteTimer data in PostgreSQL"
```

### Task 9: Durable Job Leasing and Recovery

**Files:**
- Create: `src/RouteTimer.Domain/Jobs/AnalysisJob.cs`
- Create: `src/RouteTimer.Services/Jobs/IJobQueue.cs`
- Create: `src/RouteTimer.Services/Jobs/JobDispatcher.cs`
- Create: `src/RouteTimer.Persistence/Jobs/PostgresJobQueue.cs`
- Create: `src/RouteTimer.Api/Workers/AnalysisWorker.cs`
- Test: `tests/RouteTimer.Persistence.Tests/Jobs/PostgresJobQueueTests.cs`
- Test: `tests/RouteTimer.Api.Tests/Workers/AnalysisWorkerTests.cs`

**Interfaces:**
- Consumes: `RouteTimerDbContext` and `AnalysisJob` schema from Task 8.
- Produces: enqueue, claim, renew, succeed, permanent-fail, transient-fail, and expired-lease recovery operations.

- [ ] **Step 1: Write lease and retry tests**

```csharp
[Fact]
public async Task Expired_running_job_can_be_claimed_again()
{
    var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), cancellationToken);
    var first = await queue.ClaimAsync("worker-a", clock.UtcNow, TimeSpan.FromMinutes(2), cancellationToken);
    clock.Advance(TimeSpan.FromMinutes(3));
    var second = await queue.ClaimAsync("worker-b", clock.UtcNow, TimeSpan.FromMinutes(2), cancellationToken);
    Assert.Equal(id, second!.Id);
    Assert.Equal(2, second.AttemptCount);
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --filter PostgresJobQueueTests`

Expected: FAIL because job queue methods are missing.

- [ ] **Step 3: Implement transactional leasing**

Claim with one transaction using `FOR UPDATE SKIP LOCKED`; order queued/expired jobs by creation time; set worker, lease expiry, running state, and attempt count atomically. Renew only when worker and state match. Retry transient failures with bounded backoff up to three attempts. Persist only safe error code/message.

- [ ] **Step 4: Implement the hosted worker loop**

`AnalysisWorker` creates a service scope per job, dispatches by `JobType`, renews leases during work, honours cancellation between jobs, and never lets one job exception terminate the host. Use an injectable clock/delay in tests; do not sleep in test code.

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --filter PostgresJobQueueTests`

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --filter AnalysisWorkerTests`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS.

```bash
git add src/RouteTimer.Domain/Jobs src/RouteTimer.Services/Jobs src/RouteTimer.Persistence/Jobs src/RouteTimer.Api/Workers tests/RouteTimer.Persistence.Tests/Jobs tests/RouteTimer.Api.Tests/Workers
git commit -m "feat: run analysis jobs durably"
```

### Task 10: Training, Model-Build, and Holdout-Validation Use Cases

**Files:**
- Create: `src/RouteTimer.Services/Training/TrainingUploadService.cs`
- Create: `src/RouteTimer.Services/Training/ParseTrainingJobHandler.cs`
- Create: `src/RouteTimer.Services/Models/ModelBuildJobHandler.cs`
- Create: `src/RouteTimer.Services/Models/ModelValidator.cs`
- Create: `src/RouteTimer.Services/Models/ModelBuildCoordinator.cs`
- Create: `tests/RouteTimer.Services.Tests/Training/UploadFixtures.cs`
- Test: `tests/RouteTimer.Services.Tests/Training/TrainingUploadServiceTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Models/ModelBuildJobHandlerTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Models/ModelValidatorTests.cs`

**Interfaces:**
- Consumes: parsers/cleaner/model/predictor from Tasks 4–7, persistence ports from Task 8, and jobs from Task 9.
- Produces: deduplicated retained uploads, parse jobs, coalesced immutable model builds, and leave-one-activity-out metrics.

- [ ] **Step 1: Write failing upload and immutable-version tests**

```csharp
[Fact]
public async Task Accept_batch_returns_independent_accepted_duplicate_and_invalid_results()
{
    var results = await service.AcceptAsync(UploadFixtures.MixedBatch(), cancellationToken);
    Assert.Collection(results,
        x => Assert.Equal(UploadOutcome.Accepted, x.Outcome),
        x => Assert.Equal(UploadOutcome.Duplicate, x.Outcome),
        x => Assert.Equal(UploadOutcome.Invalid, x.Outcome));
}

[Fact]
public async Task Rebuild_publishes_new_version_without_mutating_previous_model()
{
    var first = await handler.BuildAsync(cancellationToken);
    training.Add(ActivityFixtures.Eligible());
    var second = await handler.BuildAsync(cancellationToken);
    Assert.NotEqual(first.Version, second.Version);
    Assert.Equal(first, await models.GetAsync(first.Version, cancellationToken));
}
```

- [ ] **Step 2: Verify use-case tests fail**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter "TrainingUploadServiceTests|ModelBuildJobHandlerTests|ModelValidatorTests"`

Expected: FAIL because orchestration types are missing.

- [ ] **Step 3: Implement upload, parse, delete, and rebuild coordination**

Hash streams while copying to bounded memory/temp storage, persist unique raw bytes transactionally, enqueue parse jobs, and return per-file outcomes. Parse into staging samples and publish only after eligibility is known. Deletion removes the activity/upload and enqueues one coalesced rebuild. Reject concurrent rebuild publication using a PostgreSQL advisory lock and publish a new immutable version only after all bands/calibration/metrics are stored.

- [ ] **Step 4: Implement whole-activity validation**

For at least three eligible activities, rebuild a fold model without each activity, predict that activity's processed route, and calculate per-ride APE, median APE, and p90 APE. Store `PassedTarget = MedianApe <= .10`. With fewer than three activities store `InsufficientData`; never report a pass.

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter "TrainingUploadServiceTests|ModelBuildJobHandlerTests|ModelValidatorTests"`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS.

```bash
git add src/RouteTimer.Services/Training src/RouteTimer.Services/Models tests/RouteTimer.Services.Tests/Training tests/RouteTimer.Services.Tests/Models
git commit -m "feat: build and validate rider models"
```

### Task 11: Profile and Prediction Use Cases

**Files:**
- Create: `src/RouteTimer.Services/Profile/ProfileService.cs`
- Create: `src/RouteTimer.Services/Predictions/PredictionSubmissionService.cs`
- Create: `src/RouteTimer.Services/Predictions/PredictionJobHandler.cs`
- Create: `src/RouteTimer.Services/Predictions/PredictionQueryService.cs`
- Create: `tests/RouteTimer.Services.Tests/Predictions/PredictionWorkflowFixtures.cs`
- Test: `tests/RouteTimer.Services.Tests/Profile/ProfileServiceTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Predictions/PredictionWorkflowTests.cs`

**Interfaces:**
- Consumes: GPX/route processing from Tasks 2–3, model/predictor from Tasks 6–7, persistence and jobs from Tasks 8–9.
- Produces: validated profile updates, queued predictions bound to a ready model version, persistent detailed results, and immutable assumption snapshots.

- [ ] **Step 1: Write failing profile and model-snapshot tests**

```csharp
[Theory]
[InlineData(0, 10)]
[InlineData(75, 0)]
public async Task Profile_rejects_non_positive_weight(double riderKg, double bikeKg) =>
    await Assert.ThrowsAsync<ProfileValidationException>(() => service.UpdateAsync(riderKg, bikeKg, cancellationToken));

[Fact]
public async Task Submitted_prediction_keeps_model_and_weight_snapshot_after_profile_change()
{
    var queued = await submissions.SubmitAsync(PredictionFixtures.Gpx(), "route.gpx", cancellationToken);
    await profiles.UpdateAsync(80, 11, cancellationToken);
    await handler.HandleAsync(queued.JobId, cancellationToken);
    var result = await queries.GetAsync(queued.PredictionId, cancellationToken);
    Assert.Equal(75, result!.RiderWeightKg);
    Assert.Equal(queued.ModelVersion, result.ModelVersion);
}
```

- [ ] **Step 2: Verify workflow tests fail**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter "ProfileServiceTests|PredictionWorkflowTests"`

Expected: FAIL because use cases are missing.

- [ ] **Step 3: Implement profile and submission rules**

Accept rider weights `30..250 kg` and bike/equipment weights `3..60 kg`. Prediction submission requires a profile and a ready model, stores the GPX upload and model/profile/assumption snapshot in one transaction, and enqueues a prediction job. Return conflict codes `profile-required` or `model-not-ready` instead of queueing invalid work.

- [ ] **Step 4: Implement prediction processing and queries**

The handler parses/processes GPX, invokes `IRoutePredictor`, writes segments to staging, publishes totals/confidence/warnings atomically, and leaves the previous current model irrelevant to the queued prediction. Query projections return summary lists without segment payload and a detail projection with ordered segments.

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter "ProfileServiceTests|PredictionWorkflowTests"`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS.

```bash
git add src/RouteTimer.Services/Profile src/RouteTimer.Services/Predictions tests/RouteTimer.Services.Tests/Profile tests/RouteTimer.Services.Tests/Predictions
git commit -m "feat: create persistent route predictions"
```

### Task 12: Authenticated API Contracts and Endpoints

**Files:**
- Create: `src/RouteTimer.Contracts/Profile/*.cs`
- Create: `src/RouteTimer.Contracts/Training/*.cs`
- Create: `src/RouteTimer.Contracts/Models/*.cs`
- Create: `src/RouteTimer.Contracts/Predictions/*.cs`
- Create: `src/RouteTimer.Contracts/Jobs/*.cs`
- Create: `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Create: `src/RouteTimer.Api/Auth/AuthExtensions.cs`
- Create: `src/RouteTimer.Api/Endpoints/{Profile,Training,Models,Predictions,Jobs}Endpoints.cs`
- Create: `src/RouteTimer.Api/Errors/ProblemDetailsExtensions.cs`
- Create: `tests/RouteTimer.Api.Tests/Endpoints/MultipartFixtures.cs`
- Test: `tests/RouteTimer.Api.Tests/Auth/AuthorizationTests.cs`
- Test: `tests/RouteTimer.Api.Tests/Endpoints/UploadAndPredictionEndpointTests.cs`

**Interfaces:**
- Consumes: use cases from Tasks 10–11.
- Produces: the exact `/api` and health surface in the spec, stable DTOs/error codes, JWT bearer validation, upload limits, and same-origin static client fallback.

- [ ] **Step 1: Write authorization and multipart endpoint tests**

```csharp
[Theory]
[InlineData("/api/profile")]
[InlineData("/api/models/current")]
[InlineData("/api/predictions")]
public async Task Api_requires_authenticated_rider(string path)
{
    using var response = await anonymousClient.GetAsync(path);
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}

[Fact]
public async Task Training_upload_returns_202_and_per_file_outcomes()
{
    using var response = await riderClient.PostAsync("/api/training-activities", MultipartFixtures.MixedFitBatch());
    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<TrainingUploadResponse>();
    Assert.Equal(3, body!.Files.Count);
}
```

- [ ] **Step 2: Verify API tests fail**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --filter "AuthorizationTests|UploadAndPredictionEndpointTests"`

Expected: FAIL because endpoint/auth contracts are missing.

- [ ] **Step 3: Implement JWT and fallback test authentication**

Production JWT bearer settings bind `Authentication:Authority`, `Authentication:Audience`, require HTTPS metadata, validate issuer/audience/lifetime/signature, and map realm role `rider`. Integration tests replace the scheme with explicit anonymous/rider/wrong-audience principals. Apply a fallback authorization policy; call `.AllowAnonymous()` only on both health endpoints.

- [ ] **Step 4: Implement endpoints and problem details**

Map the verbs and paths from spec section 12. Use typed contract records, `202` with job identifiers for accepted work, `409` for missing profile/model readiness, `413` for size limits, `422` for semantic file validation, and RFC problem details with stable `code` extension. Stream multipart files; never bind raw bytes into endpoint DTOs. Map `MapFallbackToFile("index.html")` after API endpoints.

- [ ] **Step 5: Run API and full tests, then commit**

Run: `dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --filter "AuthorizationTests|UploadAndPredictionEndpointTests"`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS.

```bash
git add src/RouteTimer.Contracts src/RouteTimer.Api tests/RouteTimer.Api.Tests
git commit -m "feat: expose authenticated RouteTimer API"
```

### Task 13: Blazor Authentication, Dashboard, Profile, and Training UI

**Files:**
- Modify: `src/RouteTimer.Client/Program.cs`
- Modify: `src/RouteTimer.Client/App.razor`
- Create: `src/RouteTimer.Client/Auth/ApiAuthorizationMessageHandler.cs`
- Create: `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`
- Create: `src/RouteTimer.Client/Layout/MainLayout.razor`
- Create: `src/RouteTimer.Client/Pages/Dashboard.razor`
- Create: `src/RouteTimer.Client/Pages/Profile.razor`
- Create: `src/RouteTimer.Client/Pages/Training.razor`
- Create: `src/RouteTimer.Client/Components/{JobProgress,ModelStatus,ProblemMessage}.razor`
- Create: `src/RouteTimer.Client/wwwroot/appsettings.json`
- Test: `tests/RouteTimer.Client.Tests/Pages/ProfileTests.cs`
- Test: `tests/RouteTimer.Client.Tests/Pages/TrainingTests.cs`

**Interfaces:**
- Consumes: API contracts/endpoints from Task 12.
- Produces: OIDC-protected app shell, typed API client, editable weights, multi-FIT upload, per-file outcomes, activity list, delete confirmation, and durable job polling.

- [ ] **Step 1: Pin bUnit 2.9.0 and write component tests**

```csharp
[Fact]
public void Profile_prevents_submit_until_both_weights_are_valid()
{
    using var context = new BunitContext();
    context.Services.AddSingleton<IRouteTimerApiClient>(new FakeApiClient());
    var cut = context.Render<Profile>();
    cut.Find("input[name=riderWeight]").Change("75");
    cut.Find("input[name=bikeWeight]").Change("0");
    Assert.True(cut.Find("button[type=submit]").HasAttribute("disabled"));
}
```

- [ ] **Step 2: Verify client tests fail**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --filter "ProfileTests|TrainingTests"`

Expected: FAIL because pages/components are missing.

- [ ] **Step 3: Implement OIDC and typed API access**

Configure `AddOidcAuthentication` from `wwwroot/appsettings.json`, authorization code/PKCE, and the API scope. Use an authorization message handler only for the configured same-origin API base. Centralize JSON/problem parsing and cancellation in `RouteTimerApiClient`; components do not construct raw URLs or deserialize problem details themselves.

- [ ] **Step 4: Implement dashboard/profile/training states**

Render loading, empty, queued/running, success, warning, and failure states. Training accepts `.fit` only, posts all selected files together, renders each outcome, polls returned jobs with cancellation on navigation, refreshes model status, and confirms delete with text explaining rebuild impact. Dashboard shows validation target/pass state without turning low confidence into a percentage probability.

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --filter "ProfileTests|TrainingTests"`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS.

```bash
git add Directory.Packages.props src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: add rider training interface"
```

### Task 14: Prediction UI with Synchronized Map and Profiles

**Files:**
- Create: `src/RouteTimer.Client/package.json`
- Create: `src/RouteTimer.Client/package-lock.json`
- Create: `src/RouteTimer.Client/Pages/Predictions.razor`
- Create: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Create: `src/RouteTimer.Client/Components/RouteMap.razor`
- Create: `src/RouteTimer.Client/Components/RouteProfiles.razor`
- Create: `src/RouteTimer.Client/wwwroot/js/route-visualization.js`
- Create: `src/RouteTimer.Client/wwwroot/css/route-visualization.css`
- Modify: `src/RouteTimer.Client/wwwroot/index.html`
- Create: `tests/RouteTimer.Client.Tests/Pages/PredictionUiFixture.cs`
- Test: `tests/RouteTimer.Client.Tests/Pages/PredictionDetailTests.cs`
- Test: `tests/RouteTimer.Client.Tests/Components/RouteSelectionInteropTests.cs`

**Interfaces:**
- Consumes: prediction API detail DTOs from Task 12 and app shell from Task 13.
- Produces: GPX submission/history, result summaries/warnings, Leaflet map, Chart.js profiles, and bidirectional distance selection.

- [ ] **Step 1: Install locked browser dependencies and write UI tests**

Run from `src/RouteTimer.Client`: `npm install --save-exact leaflet chart.js`

```csharp
[Fact]
public void Detail_shows_assumptions_and_all_four_profile_series()
{
    using var context = PredictionUiFixture.Create();
    var cut = context.Render<PredictionDetail>();
    cut.WaitForAssertion(() => Assert.Contains("Calm, dry · moving time", cut.Markup));
    Assert.Equal(["Elevation", "Gradient", "Power", "Speed"],
        context.Visualization.InitializedSeries.Select(x => x.Name));
}
```

- [ ] **Step 2: Verify UI tests fail**

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --filter "PredictionDetailTests|RouteSelectionInteropTests"`

Expected: FAIL because prediction visual components are missing.

- [ ] **Step 3: Implement prediction creation/history/detail**

Accept `.gpx`, show `409` readiness guidance, poll job state, list past predictions newest first, and render distance/ascent/time/average speed/average power/model version/profile snapshot/confidence/warnings. Use kilometres, metres, km/h, watts, and `h:mm:ss` only at the presentation boundary.

- [ ] **Step 4: Implement local Leaflet/Chart.js interop**

Bundle locked npm assets into `wwwroot/vendor` during client build; do not load CDN scripts. `route-visualization.js` owns map/chart handles by component id, draws an attributed configurable tile layer and polyline, creates aligned datasets, and exposes `initialize`, `selectDistance`, and `dispose`. Map clicks choose the nearest segment; chart hover moves one map marker; both callbacks return sequence through `DotNetObjectReference`. Dispose every JS handle and .NET reference.

- [ ] **Step 5: Run UI and full tests, then commit**

Run: `npm ci && npm run build:vendor` from `src/RouteTimer.Client`.

Run: `dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --filter "PredictionDetailTests|RouteSelectionInteropTests"`

Run: `dotnet test RouteTimer.slnx --no-restore`

Expected: PASS.

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: visualize detailed route predictions"
```

### Task 15: Docker, Shared Caddy, Keycloak Provisioning, and End-to-End Acceptance

**Files:**
- Create: `Dockerfile`
- Create: `docker-compose.yml`
- Create: `docker-compose.test.yml`
- Create: `.env.example`
- Create: `deploy/caddy/routetimer.caddy.template`
- Create: `deploy/keycloak/realm-settings.json`
- Create: `deploy/setup-routetimer-windows.ps1`
- Create: `deploy/tests/setup-routetimer-windows.Tests.ps1`
- Create: `tests/RouteTimer.EndToEnd.Tests/AuthenticatedJourneyTests.cs`
- Create: `tests/RouteTimer.EndToEnd.Tests/HoldoutAccuracyReportTests.cs`
- Create: `README.md`

**Interfaces:**
- Consumes: complete application from Tasks 1–14 and LocalAI's external `mcp-public`, shared Caddy path, and Keycloak 26 deployment conventions.
- Produces: multi-stage image, private PostgreSQL topology, idempotent host deployment, real-login browser smoke test, model-quality acceptance report, and operator documentation.

- [ ] **Step 1: Write deployment contract tests before scripts**

Pester tests must assert that generated Compose publishes no `ports`, declares `mcp-public` as external, keeps the database only on `routetimer-internal`, renders `reverse_proxy routetimer-web:8080`, provisions realm `routetimer`, validates the whole shared Caddy config, calls `caddy reload`, and never calls `docker compose restart` for Caddy.

Run: `pwsh -NoProfile -Command "Invoke-Pester deploy/tests/setup-routetimer-windows.Tests.ps1 -Output Detailed"`

Expected: FAIL because deployment files/functions are missing.

- [ ] **Step 2: Implement Docker build and Compose health**

Use Node and .NET SDK build stages, run `npm ci`, publish standalone client assets, copy them into API `wwwroot`, and finish on the ASP.NET runtime image as a non-root user. Compose uses `postgres:16-alpine`, a named data volume, a private internal network, external `mcp-public`, secret-supplied connection/auth settings, and health checks. Add an idempotent migration command guarded by PostgreSQL advisory lock before `/health/ready` succeeds.

- [ ] **Step 3: Implement idempotent Windows deployment**

Parameters are public hostname, Keycloak URL, install roots, secret-file path, and initial rider username. Validate prerequisites; build/start RouteTimer; reconcile the realm, public PKCE SPA client, API audience, rider role, redirect/logout origins, and role assignment via `kcadm.sh`; render the Caddy drop-in into `C:\mcp-host\caddy\conf.d`; validate the entire imported config inside the running Caddy container; restore the previous fragment on validation failure; reload without restart; verify TLS/OIDC/readiness/authenticated API. Never embed passwords in generated logs or tracked files.

- [ ] **Step 4: Add end-to-end and quality acceptance tests**

`docker-compose.test.yml` adds isolated Keycloak and test credentials only for CI. Playwright logs in, saves weights, uploads programmatically generated FIT activities, waits for a ready model, uploads a generated GPX, and asserts the summary/map/four profiles. `HoldoutAccuracyReportTests` executes deterministic synthetic rides with known coefficients and normal effort, asserts median APE <=10%, and emits per-fold/median/p90 values to test output.

- [ ] **Step 5: Document, verify, and commit**

README must cover prerequisites, local development, configuration keys, FIT-vs-GPX distinction, migrations, testing, shared-Caddy deployment, Keycloak first-user setup, backup/restore, updates, rollback, and the calm/dry/road/moving-only limitations.

Run: `dotnet test RouteTimer.slnx --no-restore`

Run: `npm ci && npm run build:vendor` from `src/RouteTimer.Client`.

Run: `docker compose config --quiet`

Run: `docker build -t routetimer:test .`

Run: `pwsh -NoProfile -Command "Invoke-Pester deploy/tests/setup-routetimer-windows.Tests.ps1 -Output Detailed"`

Run: `docker compose -f docker-compose.yml -f docker-compose.test.yml up --build --wait`

Run: `dotnet test tests/RouteTimer.EndToEnd.Tests/RouteTimer.EndToEnd.Tests.csproj --no-build`

Run: `docker compose -f docker-compose.yml -f docker-compose.test.yml down`

Expected: all unit/integration/component/browser tests PASS; holdout median APE is <=10%; Compose and image build succeed; Pester proves shared-Caddy safety; no production service publishes a host port.

```bash
git add Dockerfile docker-compose.yml docker-compose.test.yml .env.example deploy tests/RouteTimer.EndToEnd.Tests README.md src/RouteTimer.Api src/RouteTimer.Client
git commit -m "feat: deploy and verify RouteTimer"
```

---

## Final Verification Checkpoint

- [ ] Run `dotnet format RouteTimer.slnx --verify-no-changes`.
- [ ] Run `dotnet test RouteTimer.slnx --no-restore` and record total/pass/fail/skip counts.
- [ ] Run the client npm, Compose, image-build, Pester, and end-to-end commands from Task 15.
- [ ] Run `git status --short` and confirm only explicitly documented local files are untracked.
- [ ] Compare the working application against all ten acceptance criteria in the design spec and record evidence for each in the final handoff.

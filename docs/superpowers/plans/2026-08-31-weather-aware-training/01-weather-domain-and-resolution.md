[← Plan overview](README.md)

# Weather Domain and Resolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add provider-neutral weather values, route anchors, interpolation, and per-sample resolution without HTTP or persistence.

**Architecture:** Immutable domain records hold weather and wind. Focused service classes convert meteorological direction, compute density/bearings, select route anchors, interpolate observations, and pair weather with cleaned samples.

**Tech Stack:** .NET 10, C# 14, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Follow the overview's Global Constraints and Stable Cross-Task Interfaces exactly.
- Keep `RouteTimer.Domain` free of service, persistence, HTTP, and JSON dependencies.
- Invalid or incomplete weather throws `ArgumentException`; it never becomes calm/dry implicitly.
- Use wind-vector interpolation, never arithmetic interpolation of degrees.

### Task 1: Add weather primitives and deterministic resolution

**Files:**

- Create: `src/RouteTimer.Domain/Weather/WeatherEnrichmentState.cs`
- Create: `src/RouteTimer.Domain/Weather/WindVector.cs`
- Create: `src/RouteTimer.Domain/Weather/WeatherCondition.cs`
- Create: `src/RouteTimer.Domain/Weather/RouteWeatherObservation.cs`
- Create: `src/RouteTimer.Services/Weather/WeatherProviderContracts.cs`
- Create: `src/RouteTimer.Services/Weather/WeatherMath.cs`
- Create: `src/RouteTimer.Services/Weather/RouteWeatherAnchorSelector.cs`
- Create: `src/RouteTimer.Services/Weather/WeatherTimeline.cs`
- Create: `src/RouteTimer.Services/Weather/WeatherResolvedActivity.cs`
- Create: `tests/RouteTimer.Domain.Tests/Weather/WeatherValueTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Weather/WeatherMathTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Weather/RouteWeatherAnchorSelectorTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Weather/WeatherTimelineTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Weather/WeatherResolvedActivityTests.cs`

**Interfaces:**

- Consumes: `GeoPoint`, `CleanedActivity`, `CleanRideSample`, and `GeoMath`/route-distance conventions already in the repository.
- Produces: the exact stable types in `README.md`, plus:

```csharp
public interface IWeatherProvider
{
    Task<IReadOnlyList<WeatherSeries>> GetHistoricalAsync(
        IReadOnlyList<WeatherLocation> locations,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WeatherSeries>> GetForecastAsync(
        IReadOnlyList<WeatherLocation> locations,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}

public static class WeatherMath
{
    public const double SpecificGasConstantDryAir = 287.05;
    public static WindVector FromMeteorological(double speedMetresPerSecond, double fromDegrees);
    public static double AirDensity(double temperatureCelsius, double surfacePressureHectopascals);
    public static double BearingDegrees(GeoPoint from, GeoPoint to);
    public static double AlongHeading(WindVector windTo, double bearingDegrees);
    public static double CrossHeading(WindVector windTo, double bearingDegrees);
}

public sealed class RouteWeatherAnchorSelector(double spacingMetres)
{
    public IReadOnlyList<WeatherLocation> Select(IReadOnlyList<CleanRideSample> samples);
}

public sealed class WeatherTimeline(IReadOnlyList<RouteWeatherObservation> observations)
{
    public WeatherCondition Resolve(DateTimeOffset at, double cumulativeDistanceMetres);
}

public static class WeatherActivityResolver
{
    public static WeatherResolvedActivity Resolve(CleanedActivity activity, WeatherTimeline timeline);
}
```

- [ ] **Step 1: Write failing wind, density, and bearing tests**

In `WeatherValueTests`, cover record validation for finite temperature, positive pressure, non-negative precipitation, finite wind components, normalized UTC valid time, and valid coordinates/distances. In service tests, cover north/east/south/west meteorological-from conversion, direction normalization, head/tail/cross components, north wraparound, and density at `15°C / 1013.25 hPa`.

```csharp
[Fact]
public void FromMeteorological_converts_a_northerly_to_southbound_air()
{
    var wind = WeatherMath.FromMeteorological(10, 0);
    Assert.Equal(0, wind.EastMetresPerSecond, 12);
    Assert.Equal(-10, wind.NorthMetresPerSecond, 12);
}

[Fact]
public void AirDensity_uses_surface_pressure_and_absolute_temperature()
{
    Assert.Equal(1.225012, WeatherMath.AirDensity(15, 1013.25), 6);
}
```

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~WeatherMathTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore --filter FullyQualifiedName~WeatherValueTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: FAIL because `RouteTimer.Services.Weather` does not exist.

- [ ] **Step 2: Implement validated weather values and math**

Validate finite values, positive pressure/absolute temperature, non-negative wind/precipitation, latitude `[-90,90]`, longitude `[-180,180]`, and UTC `DateTimeOffset` values. Convert meteorological direction with radians and a destination direction of `from + 180°`. Calculate density with `pressureHpa * 100 / (287.05 * (temperatureC + 273.15))`.

- [ ] **Step 3: Write failing anchor tests**

Use a straight synthetic ride longer than 25 km. Assert anchors at first, approximately 10 km, approximately 20 km, and last; assert a discontinuity starts a new anchor; assert invalid spacing and empty samples fail predictably.

- [ ] **Step 4: Implement anchor selection**

Walk samples in order, accumulate geodesic segment distances, reset adjacency at `CrossesDiscontinuity`, and add the first usable point, each first point at/after the next spacing threshold, each discontinuity start, and the last usable point. De-duplicate identical `(sample index, cumulative distance)` anchors and number them from zero.

- [ ] **Step 5: Write failing timeline interpolation tests**

Cover exact observation lookup, interpolation between two UTC hours, interpolation between route anchors, clamping exactly at the first/last requested boundary, rejection outside coverage, and the `359°`/`1°` case by constructing vectors before interpolation. Open-Meteo labels precipitation by the end of its preceding-hour interval: assert a sample at `10:30` selects the value labelled `11:00`, a sample exactly at `10:00` selects `10:00`, and precipitation only interpolates across distance within that selected bucket.

```csharp
[Fact]
public void Resolve_interpolates_wind_components_across_north()
{
    var timeline = Timeline(Direction(359), Direction(1));
    var condition = timeline.Resolve(Epoch.AddMinutes(30), 0);
    Assert.True(condition.Wind.NorthMetresPerSecond < -9.9);
    Assert.InRange(Math.Abs(condition.Wind.EastMetresPerSecond), 0, .01);
}
```

- [ ] **Step 6: Implement `WeatherTimeline`**

Group observations by anchor, sort each series by `ValidAt`, require a rectangular time domain, find bracketing times and distances, and interpolate temperature, pressure, and vector components. For precipitation, choose the observation whose half-open bucket `(ValidAt - 1 hour, ValidAt]` contains the requested instant, then interpolate only spatially. Do not extrapolate beyond the stored route/time rectangle.

- [ ] **Step 7: Write failing resolved-activity tests and implement**

Calculate cumulative distance and bearing for each cleaned sample. The first point of a continuous section uses the bearing to the next point; later points use the previous-to-current bearing. A one-point section is invalid for weather-aware model evidence. Resolve condition at each sample timestamp/distance and preserve the original `CleanedActivity` reference and sample order.

- [ ] **Step 8: Run focused and domain regressions**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~Weather -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Domain.Tests/RouteTimer.Domain.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: all commands exit 0.

- [ ] **Step 9: Commit and push**

```bash
git add src/RouteTimer.Domain/Weather src/RouteTimer.Services/Weather tests/RouteTimer.Domain.Tests/Weather tests/RouteTimer.Services.Tests/Weather
git commit -m "feat: add weather resolution primitives"
git push -u origin HEAD
git status --short
```

Expected: push succeeds and status is empty. If upstream already exists, use `git push` instead of `git push -u origin HEAD`.

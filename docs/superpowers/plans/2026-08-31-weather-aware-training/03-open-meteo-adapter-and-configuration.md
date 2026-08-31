[← Plan overview](README.md)

# Open-Meteo Adapter and Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement strictly validated Open-Meteo archive and forecast calls behind `IWeatherProvider`.

**Architecture:** One typed HTTP adapter builds bounded multi-coordinate requests and maps provider JSON into provider-neutral series. `WeatherOptions` validates all algorithm-affecting settings at startup and supplies a stable provider version.

**Tech Stack:** ASP.NET Core typed `HttpClient`, `System.Text.Json`, options/configuration, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Follow overview constraints. No live network tests.
- Never log full request URLs because they contain route coordinates and times.
- Explicitly request UTC, Celsius, hPa, mm, and m/s.
- A missing/null/misaligned/invalid value fails the requested batch; no calm/dry substitution.

### Task 3: Add options and the Open-Meteo HTTP adapter

**Files:**

- Create: `src/RouteTimer.Api/Weather/WeatherOptions.cs`
- Create: `src/RouteTimer.Api/Weather/OpenMeteoContracts.cs`
- Create: `src/RouteTimer.Api/Weather/OpenMeteoWeatherProvider.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Api/appsettings.json`
- Modify: `tests/RouteTimer.Api.Tests/RouteTimerApiFactory.cs`
- Create: `tests/RouteTimer.Api.Tests/Weather/WeatherOptionsTests.cs`
- Create: `tests/RouteTimer.Api.Tests/Weather/OpenMeteoWeatherProviderTests.cs`

**Interfaces:**

```csharp
public sealed record WeatherOptions(
    Uri ArchiveBaseUrl,
    Uri ForecastBaseUrl,
    string ArchiveModel,
    string ForecastModel,
    string? ApiKey,
    TimeSpan HttpTimeout,
    int MaximumLocationsPerRequest,
    int ReconciliationBatchSize,
    double AnchorSpacingMetres,
    double WetThresholdMillimetres,
    double StrongCrosswindMetresPerSecond,
    double WetDescentMultiplier,
    double ForecastDurationMargin,
    TimeSpan MaximumForecastHorizon,
    TimeSpan ForecastCacheLifetime,
    int ForecastCacheEntries)
{
    public const string ConfigurationSection = "Weather";
    public string ProviderVersion { get; }
    public static WeatherOptions Bind(IConfiguration configuration);
}
```

Defaults: public archive `https://archive-api.open-meteo.com/`, public forecast `https://api.open-meteo.com/`, models `best_match`, timeout 20 seconds, maximum 50 locations/request, reconciliation batch 25, anchor spacing 10,000 m, wet threshold 0.1 mm, crosswind threshold 3 m/s, wet multiplier 0.85, duration margin 0.5, maximum horizon 15 days, cache lifetime 5 minutes, cache entries 32.

- [ ] **Step 1: Write failing options tests**

Assert exact defaults and provider-version stability. Reject non-absolute/non-HTTP(S) URLs, non-positive timeout/counts/spacing/horizon/cache values, wet threshold below zero, crosswind below zero, multiplier outside `(0,1]`, margin below zero, blank model names, and an API key consisting only of whitespace.

- [ ] **Step 2: Implement `WeatherOptions.Bind` and register it**

Bind once in `Program.cs`, add as singleton, and configure a typed `HttpClient`. Call `RemoveAllLoggers()` on its `IHttpClientBuilder` because standard HttpClient logging includes the full coordinate-bearing URI; provider metrics arrive in Task 12 without URLs. Set timeout from options. Add the default `Weather` JSON section to appsettings and required public test URLs to `RouteTimerApiFactory.DefaultSettings` so unrelated API tests still boot.

- [ ] **Step 3: Write failing request-shape tests with a delegate handler**

For historical, assert path `/v1/archive`; for forecast, `/v1/forecast`. Assert comma-separated latitude/longitude lists remain positionally aligned, `timezone=GMT`, `wind_speed_unit=ms`, requested hourly variables, explicit model, inclusive date/time coverage, and API key only when configured. Assert more locations than the configured maximum are split into deterministic batches and recombined in original sequence order.

- [ ] **Step 4: Implement request construction**

Use `Uri.EscapeDataString` for values, invariant culture with enough coordinate precision, and `HttpCompletionOption.ResponseHeadersRead`. Do not retry inside the adapter; the historical job owns retries and forecast download reports failure. Throw a provider-specific `WeatherProviderException` containing only stable `Code`, safe message, and `IsRetryable`.

- [ ] **Step 5: Write failing response-validation tests**

Cover single- and multi-location response shapes; returned grid coordinates/elevation; strictly increasing times; UTC conversion; exact unit validation; null arrays; length mismatch; NaN/Infinity; negative wind/precipitation; direction normalization; missing coverage; HTTP 400 as permanent; HTTP 429/5xx/timeout as retryable; cancellation propagation unchanged.

Use literal minimal JSON, for example:

```json
{
  "latitude": 51.5,
  "longitude": -0.1,
  "elevation": 25.0,
  "utc_offset_seconds": 0,
  "hourly_units": {
    "time": "iso8601",
    "temperature_2m": "°C",
    "surface_pressure": "hPa",
    "precipitation": "mm",
    "wind_speed_10m": "m/s",
    "wind_direction_10m": "°"
  },
  "hourly": {
    "time": ["2026-08-31T10:00", "2026-08-31T11:00"],
    "temperature_2m": [15.0, 16.0],
    "surface_pressure": [1000.0, 999.0],
    "precipitation": [0.0, 0.2],
    "wind_speed_10m": [4.0, 5.0],
    "wind_direction_10m": [270.0, 280.0]
  }
}
```

- [ ] **Step 6: Implement JSON mapping and strict coverage**

Use explicit DTO property names. Open-Meteo may return an object for one coordinate and an array for multiple; normalize both. Validate units before values. Interpret no-offset hourly timestamps as UTC because the request forces GMT. Map direction through `WeatherMath.FromMeteorological` and preserve each original `WeatherLocation` in its output series.

- [ ] **Step 7: Run focused and host-start regressions**

```bash
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~OpenMeteoWeatherProviderTests|FullyQualifiedName~WeatherOptionsTests|FullyQualifiedName~HealthEndpointTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: pass with zero real network calls.

- [ ] **Step 8: Commit and push**

```bash
git add src/RouteTimer.Api/Weather src/RouteTimer.Api/Program.cs src/RouteTimer.Api/appsettings.json tests/RouteTimer.Api.Tests
git commit -m "feat: add Open-Meteo weather provider"
git push
git status --short
```

Expected: successful push and empty status.

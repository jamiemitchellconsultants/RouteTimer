[← Plan overview](README.md)

# Historical Enrichment Service and Job Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fetch, summarize, validate, and persist archive weather for one activity through the durable analysis-job system.

**Architecture:** A service performs one idempotent enrichment using provider-neutral interfaces. A job handler translates retryable/permanent outcomes into existing job semantics. Upload activation and startup backfill deliberately wait until Task 08.

**Tech Stack:** .NET 10 services, existing PostgreSQL job queue, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Follow overview constraints and Tasks 01–03 interfaces.
- This commit adds/registers the handler but does not change `ParseTrainingJobHandler` and does not start reconciliation.
- Provider failure never writes partial observations or a fake Ready state.
- Use the activity metadata timestamps and route anchors; do not infer weather from upload time.

### Task 4: Add historical enrichment and its job handler

**Files:**

- Create: `src/RouteTimer.Services/Weather/WeatherSummaryCalculator.cs`
- Create: `src/RouteTimer.Services/Weather/HistoricalWeatherEnrichmentService.cs`
- Create: `src/RouteTimer.Services/Weather/WeatherEnrichmentException.cs`
- Create: `src/RouteTimer.Services/Validation/WeatherEnrichmentJobException.cs`
- Create: `src/RouteTimer.Services/Training/EnrichTrainingWeatherJobHandler.cs`
- Modify: `src/RouteTimer.Domain/Jobs/AnalysisJob.cs`
- Modify: `src/RouteTimer.Services/Jobs/JobProgressReporter.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Create: `tests/RouteTimer.Services.Tests/Weather/WeatherSummaryCalculatorTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Weather/HistoricalWeatherEnrichmentServiceTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Training/EnrichTrainingWeatherJobHandlerTests.cs`
- Modify job-type exhaustive tests where compilation identifies them.

**Interfaces:**

```csharp
public enum HistoricalWeatherEnrichmentOutcome { Ready, AlreadyReady, Unavailable }

public sealed class HistoricalWeatherEnrichmentService(
    ITrainingWeatherRepository repository,
    IWeatherProvider provider,
    RouteWeatherAnchorSelector anchors,
    WeatherOptionsValues values,
    TimeProvider timeProvider)
{
    public Task<HistoricalWeatherEnrichmentOutcome> EnrichAsync(
        Guid activityId, string providerVersion, CancellationToken cancellationToken);
}

public sealed class WeatherEnrichmentException(string code, string message, bool retryable, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed class WeatherEnrichmentJobException(string code, string message, Exception? inner = null)
    : RouteTimerJobException(code, message, inner);
```

Because `RouteTimer.Services` cannot depend on API `WeatherOptions`, add a focused service value record in `WeatherProviderContracts.cs`:

```csharp
public sealed record WeatherOptionsValues(
    string ProviderVersion,
    int ReconciliationBatchSize,
    double AnchorSpacingMetres,
    double WetThresholdMillimetres,
    double StrongCrosswindMetresPerSecond,
    double WetDescentMultiplier,
    double ForecastDurationMargin,
    TimeSpan MaximumForecastHorizon,
    TimeSpan ForecastCacheLifetime,
    int ForecastCacheEntries);
```

`Program.cs` maps `WeatherOptions` into this record and registers both.

- [ ] **Step 1: Write failing summary tests**

Assert min/max temperature, maximum vector magnitude, precipitation sum without double-counting the same valid hour across anchors, and prevailing meteorological-from direction from duration/distance-weighted mean east/north vectors. Include cancelling vectors: when magnitude is effectively zero, prevailing direction is `0` and display code later treats direction as unavailable.

- [ ] **Step 2: Implement `WeatherSummaryCalculator`**

Validate non-empty observations. Group precipitation by valid hour and average spatial anchors before summing hours. Weight temperature/wind observations equally per anchor/hour cell. Convert the mean wind-to vector back to meteorological-from degrees using `atan2`; normalize to `[0,360)`.

- [ ] **Step 3: Write failing enrichment-service tests**

Cover:

- missing activity returns `Unavailable` without provider call;
- current Ready/provider-version returns `AlreadyReady`;
- stale Ready refetches;
- empty/one-point/unusable timestamps mark `Unavailable` with stable code;
- valid source selects first/10 km/last anchors and UTC hour bounds;
- series map to ordered `RouteWeatherObservation` rows and Ready summary;
- cancellation before save makes no state change;
- provider retryable/permanent exceptions preserve safe codes.

Use fakes that record exact inputs and return the Task 03 provider-neutral series.

- [ ] **Step 4: Implement the enrichment service**

Read `TrainingWeatherSource`, validate start/end and at least two usable positions, select anchors, floor start to UTC hour, ceil end to the next UTC hour, mark Pending with request time, call `GetHistoricalAsync`, flatten each series while preserving anchor sequence/distance/requested/grid position, calculate summary, and call `SaveReadyAsync` once. `Program.cs` copies `WeatherOptions.ProviderVersion` into `WeatherOptionsValues.ProviderVersion`; handler/service tests assert the same value reaches repository provenance.

If the activity is structurally unusable, call `MarkUnavailableAsync` and return `Unavailable`. Wrap provider exceptions as `WeatherEnrichmentException` without coordinates or URLs. Let caller cancellation escape unchanged.

- [ ] **Step 5: Write failing job-handler tests**

Add `JobType.EnrichTrainingWeather`. Assert handler rejects another type, reports monotonically increasing named stages, calls service with `job.SubjectId`, queues no model build yet, treats Ready/AlreadyReady/Unavailable as successful handling, rethrows retryable failure before `JobRetryPolicy.MaxAttempts`, and on the final attempt marks the activity Failed before throwing a permanent `RouteTimerJobException` with stable safe diagnostic.

- [ ] **Step 6: Implement and register the dormant handler**

Use `AnalysisJob.AttemptCount` to identify the final attempt. Earlier retryable errors must remain ordinary exceptions so `AnalysisWorker` requeues them. Permanent provider errors mark Unavailable and throw `WeatherEnrichmentJobException`. Final retryable exhaustion marks Failed and throws `WeatherEnrichmentJobException`. Inject `ITrainingWeatherRepository` into the handler for those terminal state changes. Register `ITrainingWeatherRepository`, `IJobHandler`, and required scoped/singleton services; no hosted reconciler is registered in this task.

- [ ] **Step 7: Run focused and worker regressions**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~HistoricalWeather|FullyQualifiedName~WeatherSummary|FullyQualifiedName~EnrichTrainingWeather" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter FullyQualifiedName~AnalysisWorker -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: pass; no test performs network I/O.

- [ ] **Step 8: Commit and push**

```bash
git add src/RouteTimer.Domain/Jobs src/RouteTimer.Services/Weather src/RouteTimer.Services/Training src/RouteTimer.Services/Jobs src/RouteTimer.Services/Validation src/RouteTimer.Api/Program.cs tests
git commit -m "feat: enrich training rides with archive weather"
git push
git status --short
```

Expected: successful push and empty status.

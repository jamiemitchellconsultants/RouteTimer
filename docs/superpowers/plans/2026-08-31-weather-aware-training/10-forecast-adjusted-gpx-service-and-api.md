[← Plan overview](README.md)

# Forecast-Adjusted GPX Service and API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recompute a completed prediction against a route-time forecast and return a transient timed GPX without database writes.

**Architecture:** A service loads the retained route and captured model/profile, fetches forecast series for route anchors, reruns the environment-aware predictor from one captured `now`, and maps the result directly into an in-memory GPX source. The endpoint extends the existing GPX query with `weather=current` and explicit error mapping.

**Tech Stack:** RouteTimer services/persistence, Open-Meteo adapter, minimal API, xUnit/API integration tests.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Only `timed=true&weather=current` invokes forecast logic.
- Use the prediction's captured `RiderModelId` and captured profile, never the current rider model/profile.
- Reject model versions other than `weather-v1`.
- Do not create/update predictions, segments, adjustments, jobs, rider models, or Garmin state.
- Forecast failure never returns baseline bytes.

### Task 10: Add transient forecast recomputation and endpoint

**Files:**

- Create: `src/RouteTimer.Services/Predictions/WeatherAdjustedGpxService.cs`
- Create: `src/RouteTimer.Services/Predictions/WeatherAdjustedGpxException.cs`
- Create: `src/RouteTimer.Services/Weather/ForecastWeatherCache.cs`
- Modify: `src/RouteTimer.Services/Weather/TimelineRouteEnvironment.cs`
- Modify: `src/RouteTimer.Services/Persistence/IPredictionRepository.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/PredictionRepository.cs`
- Modify: `src/RouteTimer.Services/Routes/PredictionGpxWriter.cs`
- Modify: `src/RouteTimer.Contracts/Predictions/PredictionContracts.cs`
- Modify: `src/RouteTimer.Contracts/Errors/ErrorCodes.cs`
- Modify: `src/RouteTimer.Api/Endpoints/PredictionsEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/PredictionEndpointTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Adjustments/AdjustmentJobHandlerHarness.cs`
- Modify: `tests/RouteTimer.Services.Tests/Garmin/GarminCourseServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionDeletionServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionWorkflowTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Predictions/WeatherAdjustedGpxServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Routes/PredictionGpxWriterTests.cs`

**Interfaces:**

```csharp
public sealed record PredictionWeatherExportSource(
    Guid PredictionId,
    PredictionState State,
    StoredUpload Upload,
    Guid ModelId,
    RiderProfile Profile,
    TimeSpan? BaselineMovingTime,
    string RouteName);

Task<PredictionWeatherExportSource?> GetWeatherExportSourceAsync(
    Guid predictionId, CancellationToken cancellationToken);

public sealed record WeatherAdjustedGpxFile(byte[] Content, string ContentType, string FileName);

public sealed class WeatherAdjustedGpxService
{
    public Task<WeatherAdjustedGpxFile> CreateAsync(Guid predictionId, CancellationToken cancellationToken);
}

public sealed class ForecastWeatherCache(TimeProvider timeProvider, TimeSpan lifetime, int maximumEntries)
{
    public Task<IReadOnlyList<WeatherSeries>> GetOrCreateAsync(
        IReadOnlyList<WeatherLocation> locations,
        DateTimeOffset from,
        DateTimeOffset to,
        string providerVersion,
        Func<CancellationToken, Task<IReadOnlyList<WeatherSeries>>> factory,
        CancellationToken cancellationToken);
}
```

Append `bool SupportsWeatherAdjustedDownload = false` to `PredictionSummaryResponse`; endpoint mapping sets it when state is Succeeded and captured model version equals `weather-v1`.

- [ ] **Step 1: Write failing repository-source tests**

Assert the method returns retained GPX bytes/name, prediction state, captured profile/model ID, and baseline moving time; unknown returns null. Use `AsNoTracking` and prove the read leaves no modified EF entries.

- [ ] **Step 2: Implement the read model**

Load prediction plus Upload only. Do not load persisted segments because the retained GPX is reparsed to recover the skipped leading point and exact first bearing. Use the same route-name fallback as existing GPX export.

- [ ] **Step 3: Write failing `TimelineRouteEnvironment` tests**

Construct a `WeatherTimeline`; assert `Resolve(segment, absoluteTime)` uses `segment.CumulativeDistanceMetres` and the exact passed time. This class is a thin adapter and must not know start time or predicted elapsed.

- [ ] **Step 4: Write failing service guard tests**

Assert stable exception codes for missing prediction, non-Succeeded prediction, missing baseline time, missing/invalid retained GPX, missing model, legacy model, and invalid captured model. Assert none calls forecast before all guards pass.

- [ ] **Step 5: Write failing bounded-cache tests**

Assert identical normalized locations/window/provider version coalesce to one factory call within five minutes; expiry refetches; provider-version or window changes miss; failed/cancelled factories are not cached; values are copied to immutable arrays; and inserting beyond `maximumEntries` evicts the oldest entry. The key stays in memory only and is never logged.

- [ ] **Step 6: Implement `ForecastWeatherCache`**

Use a lock plus dictionary of immutable entries and in-flight `Task` values so concurrent identical requests share one provider call. Remove failed/cancelled tasks. Normalize key coordinates with the same invariant precision used by the provider adapter. Register one bounded singleton using Task 03 options.

- [ ] **Step 7: Write failing route-time forecast tests**

With `FakeTimeProvider` fixed at `2026-08-31T10:15:00Z`, assert anchor request starts at the containing UTC hour; end covers `baseline * 1.5 + one hour` but not beyond configured horizon; forecast rather than historical operation; simulated lookups cross hours based on predicted elapsed; headwind changes timestamps while power stays fixed; wet forecast uses the predictor rule; one insufficient-window result extends once; second insufficiency fails; timeout/rate limit maps safely; cancellation propagates; and no write/job method is called.

- [ ] **Step 8: Implement transient recomputation**

Load source and `IRiderModelRepository.GetAsync(source.ModelId)`. Parse/process Upload bytes using existing GPX parser/route processor. Capture `now` once. Add `RouteWeatherAnchorSelector.Select(ProcessedRoute)` using processed cumulative distances and first/last/10 km anchors.

Request forecast through the bounded cache to `ceil(now + baseline * (1 + margin) + 1 hour)`. Flatten series into `RouteWeatherObservation`, construct timeline/environment, and call predictor using the named argument `environment: new PredictionEnvironment(now, environment, wetThreshold, wetMultiplier)`. If lookup exceeds coverage, extend once within maximum horizon and rerun from the beginning.

Map processed route samples and `PredictionResult.Segments` by sequence into `PersistedPredictionSegment` in memory; validate count/sequence/time totals as strictly as `PredictionJobHandler`. Create `PredictionGpxSource` with `StartAt = now`, forecast-adjusted description, and no persistence call.

- [ ] **Step 9: Extend GPX description/filename tests**

Add `SuggestWeatherAdjustedFileName(routeName)` returning `Kingston-to-Dorking-weather-adjusted.gpx` and respecting the 80-character stem bound. Assert description includes generated/start UTC, compact condition summary, and one wet warning when applicable; XML remains valid and timestamps strictly increase.

- [ ] **Step 10: Write failing endpoint tests**

Cover baseline URLs unchanged; `weather=current` without timed is 400; unknown weather is 400; successful content type/disposition/body; legacy conflict; forecast unavailable/incomplete 503; invalid adjusted simulation 422; not found 404; incomplete 409. Assert `SupportsWeatherAdjustedDownload` mapping on detail and summary.

- [ ] **Step 11: Implement endpoint/error mapping and DI**

Extend `GetPredictionGpxAsync` with `string? weather`. Branch to existing code when null. Compare `current` ordinal-ignore-case only after rejecting blank/unknown. Register service. Add the stable spec codes to `ErrorCodes` and use existing `ApiProblems` helpers; add a 503 helper only if none exists.

The exact codes are `weather-adjustment-requires-timed-gpx`, `weather-adjustment-unsupported-model`, `weather-forecast-unavailable`, `weather-forecast-incomplete`, and `invalid-weather-adjusted-prediction`; retain existing `prediction-not-found` and `prediction-not-complete` for those cases.

- [ ] **Step 12: Run focused service/API tests**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~WeatherAdjustedGpxServiceTests|FullyQualifiedName~ForecastWeatherCache|FullyQualifiedName~PredictionGpxWriterTests|FullyQualifiedName~TimelineRouteEnvironment" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter FullyQualifiedName~PredictionEndpointTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: pass and no live HTTP.

- [ ] **Step 13: Commit and push**

```bash
git add src/RouteTimer.Services src/RouteTimer.Persistence src/RouteTimer.Contracts src/RouteTimer.Api tests
git commit -m "feat: export timed GPX with route-time weather"
git push
git status --short
```

Expected: successful push and empty status.

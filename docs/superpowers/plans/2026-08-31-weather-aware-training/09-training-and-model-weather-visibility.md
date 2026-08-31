[← Plan overview](README.md)

# Training and Model Weather Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show per-ride enrichment state/summary and model weather-evidence counts through API contracts and Blazor UI.

**Architecture:** Persistence summaries flow through existing query services into additive contract fields. Model status gains Ready/Pending/Excluded eligible counts; UI renders stable, testable copy without exposing every weather anchor.

**Tech Stack:** ASP.NET Core contracts/endpoints, Blazor WebAssembly, xUnit, bUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Additive JSON fields only; preserve every existing field and ordering-independent consumer behavior.
- Prevailing direction is optional when the vector mean is effectively calm.
- Do not expose provider request URL, API key, grid series, or raw diagnostics.
- Use `RouteTimerFormat` for temperature/wind/precipitation formatting; add focused methods there.

### Task 9: Add weather status contracts, endpoints, and UI

**Files:**

- Modify: `src/RouteTimer.Services/Persistence/ITrainingActivityRepository.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/TrainingActivityRepository.cs`
- Modify: `src/RouteTimer.Contracts/Training/TrainingActivityContracts.cs`
- Modify: `src/RouteTimer.Contracts/Models/ModelContracts.cs`
- Modify: `src/RouteTimer.Services/Models/ModelStatusResult.cs`
- Modify: `src/RouteTimer.Services/Models/ModelStatusService.cs`
- Modify: `src/RouteTimer.Api/Endpoints/TrainingEndpoints.cs`
- Modify: `src/RouteTimer.Api/Endpoints/ModelsEndpoints.cs`
- Modify: `src/RouteTimer.Client/Formatting/RouteTimerFormat.cs`
- Modify: `src/RouteTimer.Client/Pages/Training.razor`
- Modify: `src/RouteTimer.Client/Pages/TrainingDetail.razor`
- Modify: `src/RouteTimer.Client/Components/ModelStatus.razor`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/TrainingEndpointTests.cs`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/ModelEndpointTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/TrainingPageTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/TrainingDetailPageTests.cs`
- Create: `tests/RouteTimer.Client.Tests/ModelStatusComponentTests.cs`

**Interfaces:**

Append to `TrainingActivitySummary` and `TrainingActivitySummaryResponse`:

```csharp
string WeatherState,
string? WeatherDiagnosticCode,
double? MinimumTemperatureCelsius,
double? MaximumTemperatureCelsius,
double? MaximumWindSpeedMetresPerSecond,
double? PrevailingWindDirectionDegrees,
double? PrecipitationTotalMillimetres
```

Give every appended parameter a default (`"Pending"` for state and `null` for the others) so existing fixture constructors remain source-compatible. Repository and endpoint production mappings must always supply real values.

Append to `ModelStatusResult` and `ModelStatusResponse`:

```csharp
int WeatherReadyEligibleActivities,
int WeatherPendingEligibleActivities,
int WeatherExcludedEligibleActivities
```

Give the three appended response/result counts default `0` values so unchanged client fixtures remain source-compatible.

- [ ] **Step 1: Write failing repository/query and endpoint tests**

Assert all four states and optional summaries map exactly. Assert ModelStatus returns weather counts whether a current model exists or not. Keep authorization and existing response assertions.

- [ ] **Step 2: Implement additive service/contracts/endpoint mapping**

Get weather summary columns in the existing summary query without loading observation rows. `ModelStatusService` calls `GetWeatherCountsAsync` once alongside existing counts. Use enum names on service records and strings over the contract boundary, matching existing conventions.

- [ ] **Step 3: Write failing formatting tests**

Add exact invariant display behavior: one decimal Celsius with `°C`, one decimal m/s, one decimal mm, and 16-point compass labels from degrees. Null stays the existing em dash. Test `0`, `11.25`, `348.75`, and normalized `360` boundaries.

- [ ] **Step 4: Implement formatting methods**

Add `Temperature`, `WindSpeed`, `Precipitation`, and `CompassDirection`. Do not use current culture for compass mapping or change existing formatters.

- [ ] **Step 5: Write failing Training UI tests**

Assert exact state copy and test IDs:

- `weather-state-{activityId}` for Pending/Ready/Failed/Unavailable;
- `weather-summary-{activityId}` only when Ready;
- detail rows for temperature range, prevailing wind/max speed, precipitation, and diagnostic;
- failed copy says the ride is excluded from the rider model;
- pending copy says historical weather is being added.

- [ ] **Step 6: Implement Training list/detail display**

Keep weather secondary to quality/route metrics. Render a compact summary on list and full fields on detail. Safe diagnostic codes pass through `RouteTimerText.Sentence`; do not render arbitrary provider messages.

- [ ] **Step 7: Write failing model-status UI tests and implement**

Render Ready, Pending, Excluded counts with stable test IDs. When pending > 0 and the current model algorithm is not `weather-v1`, show “The existing rider model remains active while historical weather is added.” When pending is zero and `weather-v1` is current, do not show that message.

- [ ] **Step 8: Run API and client suites focused on changed surfaces**

```bash
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~TrainingEndpointTests|FullyQualifiedName~ModelEndpointTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~TrainingPageTests|FullyQualifiedName~TrainingDetailPageTests|FullyQualifiedName~ModelStatus" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: pass.

- [ ] **Step 9: Commit and push**

```bash
git add src/RouteTimer.Services src/RouteTimer.Persistence src/RouteTimer.Contracts src/RouteTimer.Api/Endpoints src/RouteTimer.Client tests
git commit -m "feat: show training weather and model evidence status"
git push
git status --short
```

Expected: successful push and empty status.

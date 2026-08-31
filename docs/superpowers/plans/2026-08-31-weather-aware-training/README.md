# Weather-Aware Training and Timed Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild rider models from weather-corrected training evidence and provide an opt-in, forecast-adjusted timed GPX download without changing ordinary predictions.

**Architecture:** Historical Open-Meteo observations are persisted beside immutable training samples and resolved into per-interval environmental conditions during model building. The predictor gains an optional environment seam whose default is the existing calm/dry behavior; the same seam powers a transient route-time forecast export that writes no database state.

**Tech Stack:** .NET 10, C# 14, ASP.NET Core minimal APIs, EF Core 10/Npgsql/PostgreSQL, Blazor WebAssembly, xUnit, bUnit, Open-Meteo JSON APIs.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Read the complete spec and the current task file before editing.
- Do not execute on `main`. Work on one feature branch/worktree; if the branch has no upstream, the first task uses `git push -u origin HEAD`, and later tasks use `git push`.
- Before each task, run `git status --short`; stop if it contains changes not produced by the current task.
- Use test-driven development: add the named failing tests, observe the expected failure, implement the minimum complete behavior, then run focused and regression tests.
- Never call live Open-Meteo from an automated test. Inject `IWeatherProvider`, `HttpMessageHandler`, and `TimeProvider` fakes.
- Historical weather uses `/v1/archive`; immediate route-time weather uses `/v1/forecast`.
- Request `temperature_2m`, `surface_pressure`, `precipitation`, `wind_speed_10m`, and `wind_direction_10m` in UTC and SI-friendly units.
- Recorded FIT samples and recorded watts remain immutable. Weather never scales typical-power evidence.
- Ordinary predictions and pacing adjustments remain road/calm/dry and must make no weather request.
- Wet means precipitation `>= 0.1 mm` in the applicable preceding-hour bucket. Wet forecast segments use descent multiplier `0.85`; they do not change power or Crr.
- Do not hand-edit `Narrative.md`. The feature PR must carry `narrative-required` and the exact `## Narrative Context`, `## Narrative Decision`, and `## Narrative Consequences` body headings.
- Every task ends with fresh verification, one focused commit, and a successful push. Do not combine two task files into one commit.

## Stable Cross-Task Interfaces

These names are fixed for the whole series. If an implementation discovers a compile-time need to alter one, update this README and every later task that consumes it in the same commit.

```csharp
// RouteTimer.Domain/Weather
public enum WeatherEnrichmentState { Pending, Ready, Failed, Unavailable }
public sealed record WindVector(double EastMetresPerSecond, double NorthMetresPerSecond);
public sealed record WeatherCondition(
    double TemperatureCelsius,
    double SurfacePressureHectopascals,
    double PrecipitationMillimetres,
    WindVector Wind);
public sealed record RouteWeatherObservation(
    int AnchorSequence,
    double CumulativeDistanceMetres,
    GeoPoint RequestedPosition,
    GeoPoint GridPosition,
    DateTimeOffset ValidAt,
    WeatherCondition Condition);

// RouteTimer.Services/Weather
public sealed record WeatherLocation(int Sequence, double CumulativeDistanceMetres, GeoPoint Position);
public sealed record WeatherSeries(WeatherLocation Location, GeoPoint GridPosition, IReadOnlyList<WeatherSeriesPoint> Points);
public sealed record WeatherSeriesPoint(DateTimeOffset ValidAt, WeatherCondition Condition);
public interface IWeatherProvider { /* historical and forecast methods from the spec */ }
public sealed record WeatherResolvedSample(
    CleanRideSample Sample,
    double CumulativeDistanceMetres,
    double BearingDegrees,
    WeatherCondition Condition);
public sealed record WeatherResolvedActivity(CleanedActivity Activity, IReadOnlyList<WeatherResolvedSample> Samples);

// RouteTimer.Services/Persistence
public sealed record TrainingActivityModelEvidence(
    Guid ActivityId,
    CleanedActivity Activity,
    WeatherEnrichmentState WeatherState,
    string? WeatherProviderVersion,
    IReadOnlyList<RouteWeatherObservation> WeatherObservations);
```

## Execution Order

| Task | Deliverable | Depends on |
|---|---|---|
| [01](01-weather-domain-and-resolution.md) | Weather value types, vector math, anchor selection, interpolation, resolved activities | approved spec |
| [02](02-weather-persistence-and-migration.md) | Activity weather state, observation table, repository methods, migration | 01 |
| [03](03-open-meteo-adapter-and-configuration.md) | Validated archive/forecast HTTP adapter and options | 01 |
| [04](04-historical-enrichment-service-and-job.md) | Archive enrichment service and dormant job handler | 01–03 |
| [05](05-environment-aware-predictor.md) | Optional environment seam with bit-for-bit calm regression | 01 |
| [06](06-weather-aware-physics-calibration.md) | Apparent-wind/density calibration, wet exclusion | 01, 05 |
| [07](07-weather-aware-descent-model.md) | Dry calm-equivalent descent learning | 01, 06 |
| [08](08-weather-aware-build-validation-and-backfill.md) | Activate upload enrichment, reconciliation, build gating, weather-aware validation | 02, 04–07 |
| [09](09-training-and-model-weather-visibility.md) | Weather summaries/counts in contracts, endpoints, Training UI | 02, 08 |
| [10](10-forecast-adjusted-gpx-service-and-api.md) | Transient route-time forecast recomputation and API | 03, 05, 08 |
| [11](11-forecast-adjusted-download-ui.md) | Checkbox, fetch/blob download, progress and errors | 10 |
| [12](12-operations-telemetry-and-release-verification.md) | Privacy/runbook/config/telemetry, full regression and rollout checks | 01–11 |

Tasks are ordered commits, not parallel work. Each file is self-contained enough for a fresh agent to execute after the preceding commits are present.

## Spec Coverage Map

| Approved spec section | Owning task files |
|---|---|
| Chosen architecture and immutable evidence boundary | 01, 02, 04, 08 |
| Provider contract, units, validation, batching | 01, 03 |
| Persistence/state, deletion, migration, provenance | 02 |
| Enrichment jobs, retries, backfill, build gating | 04, 08 |
| Typical power unchanged | 08 plus regression in 01/06 |
| Air density/apparent wind physics calibration | 05, 06 |
| Dry calm-equivalent descent limits | 07 |
| Weather-aware leave-one-out validation | 08 |
| Calm/dry ordinary prediction boundary | 05, 08 |
| Route-time forecast recomputation and GPX API | 10 |
| Checkbox, browser download, legacy guidance | 11 |
| API errors and no silent fallback | 03, 04, 10, 11 |
| Configuration and short-lived cache | 03, 10 |
| Training/model status visibility | 09 |
| Privacy, metrics, deployment, backup, rollback | 12 |
| Deterministic test coverage and all acceptance criteria | every task; final audit in 12 |

## Completion Definition

The series is complete only when Task 12's full solution tests pass, `git status --short` is empty, all twelve task commits are present on the feature branch's remote upstream, and the decision-bearing PR uses the repository narrative contract.

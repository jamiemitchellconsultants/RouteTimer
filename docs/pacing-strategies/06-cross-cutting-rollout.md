# Cross-Cutting Rollout Plan — All Five Strategies

**Date:** 2026-08-27  
**Status:** Plan only — no code changes

---

## Shared Infrastructure (build once, used by all strategies)

### 1. Discriminated union in contracts

```csharp
// RouteTimer.Contracts/Predictions/PacingStrategyRequest.cs
// New file — replaces nothing existing
public abstract record PacingStrategyRequest(string Type);

public sealed record SegmentSpecificGainsRequest(IReadOnlyList<SegmentGainRuleRequest> Rules, bool IncludeBaseline)
    : PacingStrategyRequest("segment-specific-gains");

public sealed record NpIfTargetRequest(double TargetIntensityFactor, double FtpWatts, string ScalingMode, bool IncludeBaseline)
    : PacingStrategyRequest("np-if-target");

public sealed record TimeTargetRequest(double TargetMovingSeconds, string DistributionMode, bool IncludeFeasibilityReport, bool IncludeBaseline)
    : PacingStrategyRequest("time-target");

public sealed record RpeZoneStrategyRequest(string Scheme, double? FtpWatts, IReadOnlyList<ZoneAssignmentRequest> Assignments, bool IncludeZoneDistributionReport, bool IncludeBaseline)
    : PacingStrategyRequest("rpe-zone-shift");

public sealed record MatchBurningRequest(double? CriticalPowerWatts, double? WPrimeJoules, IReadOnlyList<BurnWindowRequest> BurnWindows, ConservationPhaseRequest ConservationPhase, RecoveryPhaseRequest RecoveryPhase, bool IncludeFatigueReport, bool IncludeBaseline)
    : PacingStrategyRequest("variable-match-burning");
```

Serialised via `System.Text.Json` with a `[JsonDerivedType]` / `[JsonPolymorphic]` on `PacingStrategyRequest`.

### 2. API submission extension

`POST /api/predictions` changes from form-only to a multipart request:
- Part 1: `file` (GPX)
- Part 2: `strategy` (optional JSON; omit for baseline-only)

Or introduce a parallel endpoint `POST /api/predictions/paced` that accepts multipart with strategy, keeping the existing endpoint unchanged for backwards compatibility. **Recommendation: parallel endpoint.**

### 3. Database migrations (ordered)

| Migration | Contents |
|-----------|----------|
| M1 | `strategy_type varchar(50)`, `strategy_json jsonb` on `predictions` |
| M2 | `prediction_adjusted_segments` table (mirrors `prediction_segments`) |
| M3 | `adjusted_moving_seconds`, `adjusted_average_speed_mps`, `adjusted_average_power_watts` on `predictions` |
| M4 | NP/IF metadata columns on `predictions` |
| M5 | Time-target metadata columns on `predictions` |
| M6 | Zone metadata columns on `predictions` + `zone_number` on `prediction_adjusted_segments` |
| M7 | Match-burning metadata columns on `predictions` + `strategy_phase`, `strategy_wprime_balance` on `prediction_adjusted_segments` |

Migrations are independent of which strategies are built first. Apply all of M1–M3 in the first shared infrastructure PR; M4–M7 can be per-strategy PRs.

### 4. Shared service interface

```csharp
// RouteTimer.Services/Predictions/PacingStrategies/
public interface IPacingStrategyHandler
{
    string StrategyType { get; }
    Task<PacingStrategyResult> RunAsync(
        ProcessedRoute route,
        RiderProfile profile,
        RiderModel model,
        PredictionResult baselineResult,
        PacingStrategyRequest request,
        CancellationToken cancellationToken);
}

public sealed record PacingStrategyResult(
    PredictionResult? AdjustedResult,   // null = no adjustment (strategy is no-op)
    PredictionResult? BaselineResult,   // populated if IncludeBaseline = true
    IReadOnlyDictionary<string, object?> Metadata); // strategy-specific; serialised to strategy_*_json columns
```

`PredictionJobHandler` uses a DI-injected `IEnumerable<IPacingStrategyHandler>` keyed by `StrategyType`.

### 5. `ScaledPowerLookup` base class

All strategies need to pass a custom power source into `RoutePredictor`. Introduce:

```csharp
// RouteTimer.Services/Predictions/PacingStrategies/
public abstract class ScaledPowerLookup
{
    protected readonly PowerLookup _baseline;
    public ScaledPowerLookup(PowerLookup baseline) => _baseline = baseline;
    public abstract PowerEstimate GetWatts(double gradient, TimeSpan elapsed, int segmentSequence);
}
```

`IRoutePredictor.Predict` gains an optional `ScaledPowerLookup? overlay` parameter (defaulting to null for backwards compatibility). When non-null, the predictor calls `overlay.GetWatts` instead of `_baseline.GetWatts`.

---

## Recommended Build Order

Given the dependency graph:

```
Shared infra (M1–M3, ScaledPowerLookup, IPacingStrategyHandler)
       ↓
Strategy 1: Segment-Specific Gains      ← simplest; validates pipeline
Strategy 3: Time Target Mode            ← independent; highest rider value
Strategy 2: NP/IF Target                ← builds on ScaledPowerLookup pattern
Strategy 4: RPE/Zone Shift              ← introduces ZoneResolver used by Strategy 5
Strategy 5: Variable Match-Burning      ← most complex; reuses ZoneResolver
```

Strategies 1 and 3 can be built in parallel if two developers are available.

---

## Feature Flag Strategy

A single parent flag `pacing-strategies-enabled` gates all strategy UI and the `POST /api/predictions/paced` endpoint. Individual flags `pacing-strategy-<type>-enabled` gate each strategy independently for gradual rollout.

---

## Summary of New Types (Domain Layer)

| Type | Strategy |
|------|----------|
| `SegmentGainRule`, `SegmentGainsStrategy` | S1 |
| `NpIfStrategy`, `NpIfScalingMode` | S2 |
| `TimeTargetStrategy`, `TimeTargetDistributionMode`, `TimeTargetFeasibilityReport` | S3 |
| `RpeZoneStrategy`, `ZoneAssignment`, `ZoneDistributionReport` | S4 |
| `MatchBurningStrategy`, `BurnWindow`, `FatigueReport` | S5 |

## Summary of New Services (Services Layer)

| Service | Strategy |
|---------|----------|
| `SegmentGainsPowerLookup` | S1 |
| `NpIfScaler` | S2 |
| `TimeTargetScaler` | S3 |
| `RpeZoneScaler`, `ZoneResolver` | S4 |
| `MatchBurningCpEstimator`, `MatchBurningWPrimeTracker`, `MatchBurningPowerLookup` | S5 |
| `ScaledPowerLookup` (base), `IPacingStrategyHandler` | Shared |

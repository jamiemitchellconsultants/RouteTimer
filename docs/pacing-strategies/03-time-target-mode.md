# Strategy 3: Time Target Mode

**Date:** 2026-08-27  
**Status:** Plan only — no code changes

---

## 1. Purpose

The rider specifies a goal finish time for the route (e.g. "I want to finish in 4 h 30 min"). The system works backwards: it finds the global power scale factor S that makes the physics simulation produce the target time, then reports the required per-segment watts, whether those watts are physiologically feasible (compared to the personal power model), and how the effort is distributed across gradient zones.

This is the most intuitive strategy for event pacing: "what do I need to put out to make my goal time?"

---

## 2. Architecture

### 2.1 Placement in the pipeline

```
SubmitPrediction (API)
    ↓ PacingStrategy = TimeTarget { targetMovingSeconds: 16200 }
PredictionSubmissionService  → stores in strategy_json
PredictionJobHandler
    ↓
TimeTargetScaler.Solve(baseline, targetTime)
    → S via bisection: RoutePredictor re-run with ScaledPowerLookup(S)
    → converge until |predicted time - targetTime| < toleranceSeconds
RoutePredictor.Predict [re-run for each bisection step, or use analytical approximation]
PredictionPublication + adjusted segments + feasibility analysis
```

The critical difference from Strategy 2 is that the **objective function is route time**, not NP. Route time is non-trivially related to power because the physics are non-linear (aero drag is quadratic in speed). Bisection on S still converges because time is monotone-decreasing in S.

### 2.2 New types

```csharp
// RouteTimer.Domain/Predictions/PacingStrategies/
public sealed record TimeTargetStrategy(
    TimeSpan TargetMovingTime,
    TimeTargetDistributionMode DistributionMode,
    bool IncludeFeasibilityReport);

public enum TimeTargetDistributionMode
{
    Proportional,     // scale all bands by same S (default)
    ClimbFocused,     // allow extra push on climbs, conserve elsewhere
    EvenEffort        // attempt constant perceived effort (RPE-like)
}

public sealed record TimeTargetFeasibilityReport(
    double ScaleFactor,
    TimeSpan AchievedMovingTime,
    IReadOnlyList<FeasibilityBandSummary> BandSummaries,
    FeasibilityVerdict Verdict);

public sealed record FeasibilityBandSummary(
    string GradientBand,
    double RequiredWatts,
    double ModelBaselineWatts,
    double ScaledVsModelRatio,   // > 1 = harder than model typical
    ConfidenceLevel ModelConfidence);

public enum FeasibilityVerdict { Achievable, Challenging, Extreme, Impossible }
```

### 2.3 New service

```csharp
// RouteTimer.Services/Predictions/PacingStrategies/
public sealed class TimeTargetScaler
{
    // Finds S in [minS, maxS] s.t. Predict(S × model_watts) moves within toleranceSeconds of target.
    // Uses bisection over RoutePredictor re-runs.
    public TimeTargetScaleResult Solve(
        ProcessedRoute route,
        RiderProfile profile,
        RiderModel model,
        TimeTargetStrategy strategy,
        IRoutePredictor predictor,
        int maxIterations = 50,
        double toleranceSeconds = 30);

    // Builds feasibility report comparing required watts to model bands.
    public TimeTargetFeasibilityReport BuildFeasibilityReport(
        PredictionResult adjustedResult,
        RiderModel model,
        double scaleFactor);
}
```

### 2.4 Contract changes

```json
{
  "strategy": {
    "type": "time-target",
    "targetMovingSeconds": 16200,
    "distributionMode": "proportional",
    "includeFeasibilityReport": true,
    "includeBaseline": true
  }
}
```

`PredictionDetailResponse` gains:
- `StrategyFeasibility?: FeasibilityReportResponse`

---

## 3. Algorithm Steps

### 3.1 Proportional distribution

1. Run baseline prediction → `baselineTime`.
2. If `targetTime == baselineTime` (within tolerance), S = 1.0, done.
3. Bisection:
   - `lo_S = 0.3` (very slow), `hi_S = 4.0` (extreme effort)
   - `predict(S)` = re-run `RoutePredictor` with a `ScaledPowerLookup` returning `modelWatts × S`
   - Narrow `[lo_S, hi_S]` based on sign of `predict(S_mid).MovingTime - targetTime`
   - Terminate when `|predict(S).MovingTime - targetTime| < toleranceSeconds` or iterations exhausted
4. Compute feasibility report from converged S and adjusted result.

### 3.2 Climb-focused distribution

Rather than a global scalar, define:
- `S_climb` (applied to gradient bands ≥ 3 %)
- `S_flat` = function of `S_climb` such that overall time matches target

This reduces to a 1-D search over `S_climb` where `S_flat` is solved analytically from the time contribution of flat vs climb segments. More complex but avoids unrealistic flat-segment overreach.

Algorithm:
1. Partition segments into `climb` (gradient ≥ 3 %) and `other`.
2. Compute time contribution fractions: `t_climb / t_total`, `t_other / t_total` from baseline.
3. Parameterise: user sets a climb bias ∈ [1.0, 2.0]. Effective `S_climb = bias × S_base`; `S_flat` solves the time equation numerically.
4. Bisect on `S_base`.

### 3.3 Even effort distribution (advanced)

Computes a per-segment watt target such that the power-to-weight ratio adjusted for gradient produces equal "metabolic cost" per unit time across all segments. This approximates even RPE. See Strategy 4 (RPE/Zone Shift) for related approaches.

For this strategy, even-effort mode is a stretch goal; implement proportional first.

### 3.4 Feasibility report

After convergence:
- For each gradient band, compute `requiredWatts = model.Band[g].TypicalWatts × S`.
- Compare to `model.Band[g].TypicalWatts` (the rider's demonstrated capacity).
- Flag each band: `Achievable` (S ≤ 1.2), `Challenging` (1.2–1.5), `Extreme` (1.5–2.0), `Impossible` (> 2.0).
- Aggregate to route-level `FeasibilityVerdict` (worst band verdict).

---

## 4. Data Model Changes

### 4.1 Shared strategy columns

`strategy_type`, `strategy_json` (from Strategy 1 / 2 if built first).

### 4.2 Time target metadata

```sql
strategy_target_moving_seconds   double precision null,
strategy_achieved_moving_seconds double precision null,
strategy_scale_factor            double precision null,
strategy_feasibility_verdict     varchar(20) null,
strategy_feasibility_json        jsonb null  -- full FeasibilityReport for the UI
```

### 4.3 Adjusted segments

`prediction_adjusted_segments` (shared).

---

## 5. UX Flow

1. "Pacing strategy" panel → "Time Target".
2. Target time input: hours + minutes picker, or direct text "4:30:00".
3. Distribution mode: radio buttons (Proportional / Climb-focused with bias slider).
4. "Include feasibility report" checkbox (default on).
5. On submit: result page shows:
   - Required scale factor ("You would need to ride 14% harder than your typical power")
   - Feasibility banner: Achievable / Challenging / Extreme / Impossible with colour coding
   - Per-band feasibility table (gradient zone, required W, your typical W, ratio)
   - Baseline vs adjusted time/speed chart
   - Call to action if Extreme/Impossible: "Try a more realistic target"

---

## 6. Edge Cases

| Case | Handling |
|------|----------|
| Target time > baseline (easy day) | S < 1.0; allowed; label "recovery effort" |
| Target time impossible (e.g. 10 min for a 100 km route) | Bisection hits `hi_S = 4.0`; return `Impossible` verdict with achieved time at max S |
| Target time ≤ 0 | Return 400 `time-target-invalid` |
| Target time > 48 hours | Return 400 `time-target-too-large` |
| Baseline prediction fails (bad GPX) | Upstream validation catches before strategy step |
| Convergence fails within maxIterations | Return `strategy_converged: false`; return closest result with warning `time-target-convergence-failed` |
| Tolerance too tight for route discretisation | Default tolerance 30 s is coarse enough; document in API |
| S < 0.3 (extremely easy target) | S floored at 0.3; warn `time-target-very-low-effort`; feasibility shows "easy" |
| Climb-focused mode on a flat route (no segments ≥ 3 %) | Fall through to proportional mode; warn `time-target-no-climbs-climb-mode-ignored` |

---

## 7. Validation Approach

### Unit tests
- `TimeTargetScaler.Solve` on a flat fixture returns S that achieves target time within 30 s
- `TimeTargetScaler.Solve` on a mountainous fixture converges correctly
- S = 1.0 when target equals baseline
- Feasibility report bands map correctly to expected verdicts at known S values
- Climb-focused mode produces different S_climb vs S_flat

### Integration tests
- Full job pipeline: strategy stored, converged result published, feasibility JSON stored
- Without strategy: no regression

### Back-testing validation
- Select 20 historical rides. For each, set `targetTime = actual time`. Verify scaler converges to S ≈ 1.0 (rider's actual effort). Tolerate ±5 % deviation (accounts for non-pacing behaviour in training data).

---

## 8. Rollout Sequencing

| Phase | Work | Gate |
|-------|------|------|
| P0 | Domain types: `TimeTargetStrategy`, feasibility records | PR review |
| P0 | `TimeTargetScaler.Solve` (proportional) + unit tests | All unit tests green |
| P0 | `TimeTargetScaler.BuildFeasibilityReport` + unit tests | All unit tests green |
| P1 | Contract: `TimeTargetStrategy` in submission request; `FeasibilityReportResponse` | PR review |
| P1 | DB migration: time-target metadata columns | Migration tested |
| P1 | `PredictionSubmissionService` serialisation | Integration test |
| P2 | `PredictionJobHandler`: invoke `TimeTargetScaler`, publish adjusted + feasibility | Integration test |
| P2 | `PredictionQueryService` returns feasibility in `PredictionDetailResponse` | API contract test |
| P3 | Climb-focused distribution mode | Unit + integration tests |
| P3 | UI: target time picker, distribution mode, feasibility report display | Manual smoke test |
| P4 | Back-test validation | ≤ 5 % S deviation on historical rides |
| P4 | Even-effort mode (stretch) | Optional |
| P4 | Feature flag removal / GA | Sign-off |

**Dependencies:** Shared infrastructure from Strategies 1/2 (strategy columns, adjusted segments table).

# Strategy 2: Normalized Power / Intensity Factor Target

**Date:** 2026-08-27  
**Status:** Plan only — no code changes

---

## 1. Purpose

Normalized Power (NP) is a fatigue-weighted power metric: `NP = (mean of 30-second rolling average^4)^0.25`. Intensity Factor (IF) is `NP / FTP`. A rider can say "I want to ride this route at IF 0.85" (a moderately hard tempo effort) and the system will back-calculate the per-segment power targets needed to achieve that whole-route IF while respecting the gradient-dependent power ratios from the personal model.

This strategy is meaningful for riders who train with power meters and already know their FTP. It converts a physiological goal into concrete segment wattage and predicted time.

---

## 2. Architecture

### 2.1 Placement in the pipeline

```
SubmitPrediction (API)
    ↓ PacingStrategy = NpIfTarget { targetIF: 0.85, ftpWatts: 280 }
PredictionSubmissionService  → stores in strategy_json
PredictionJobHandler
    ↓
NpIfScaler resolves a global power scale factor S such that
  NP(S × model_watts_per_segment) ≈ targetIF × ftpWatts
RoutePredictor.Predict(route, profile, model) [unmodified]
    ↓ post-processing: scale each segment's watts by S
NpIfAdjustedResult wraps PredictionResult
```

Unlike Strategy 1, the scale factor S is computed **once for the whole route** via iteration, then applied uniformly. This preserves the *relative* gradient ratios from the power model while shifting the overall effort level.

### 2.2 New types

```csharp
// RouteTimer.Domain/Predictions/PacingStrategies/
public sealed record NpIfStrategy(
    double TargetIntensityFactor,  // e.g. 0.85
    double FtpWatts,               // rider's current FTP
    NpIfScalingMode ScalingMode);  // Proportional | Additive

public enum NpIfScalingMode
{
    Proportional,  // multiply all segment watts by S (default)
    Additive       // add a flat delta to all segment watts
}
```

### 2.3 New service

```csharp
// RouteTimer.Services/Predictions/PacingStrategies/
public sealed class NpIfScaler
{
    // Given a baseline PredictionResult (watts per segment + duration per segment),
    // finds S via bisection search such that
    //   ComputeNP(scaledWatts, segmentDurations) ≈ targetIF × ftpWatts
    // Returns the converged scale factor and the resulting NP.
    public NpIfScaleResult Solve(
        PredictionResult baseline,
        NpIfStrategy strategy,
        int maxIterations = 100,
        double toleranceWatts = 0.5);

    // Computes NP from segment (watts, duration) pairs using the 30-second rolling window rule
    // approximated at segment granularity.
    public static double ComputeNP(IReadOnlyList<(double Watts, TimeSpan Duration)> segments);
}
```

### 2.4 Contract changes

```json
{
  "strategy": {
    "type": "np-if-target",
    "targetIntensityFactor": 0.85,
    "ftpWatts": 280,
    "scalingMode": "proportional",
    "includeBaseline": true
  }
}
```

`PredictionSummaryResponse` gains:
- `StrategyType?: string`
- `StrategyNormalizedPowerWatts?: double`
- `StrategyIntensityFactor?: double`
- `StrategyFtpWatts?: double`
- `StrategyScaleFactor?: double`

---

## 3. Algorithm Steps

### 3.1 NP computation (segment-granularity approximation)

True NP requires a continuous 30-second rolling window. With route segments of variable length, the approach is:

1. Build a synthetic 1-second time series by spreading each segment's watts uniformly across its duration.
2. Apply a 30-second SMA over the series (centred or trailing; trailing is conservative).
3. Raise each value to the 4th power.
4. Average the 4th powers.
5. Take the 4th root → NP.

This is O(totalRouteSeconds) which for a 6-hour ride is ~21,600 operations — fast.

### 3.2 Bisection for scale factor S

```
lo = 0.1, hi = 5.0 (clamped safety bounds)
targetNP = targetIF × ftpWatts

repeat until |NP(S) - targetNP| < toleranceWatts:
    S_mid = (lo + hi) / 2
    scaledSegments = baseline segments with watts × S_mid
    np = ComputeNP(scaledSegments)
    if np < targetNP: lo = S_mid
    else: hi = S_mid
```

Convergence is guaranteed because `NP(S)` is monotone increasing in `S` for proportional scaling. Typically < 30 iterations to ±0.5 W.

### 3.3 Apply and record

- Multiply each `PredictionSegment.PowerWatts` by S.
- Re-run physics for speed/time: either **re-run `RoutePredictor`** with a `ScaledPowerLookup` that returns `baseline.Watts × S`, or apply a simpler post-hoc scaling (see edge cases — re-run preferred for accuracy).
- Record `ScaleFactor`, `AchievedNP`, `AchievedIF` in the publication.

---

## 4. Data Model Changes

### 4.1 Strategy columns (shared with Strategy 1)

`strategy_type varchar(50)`, `strategy_json jsonb` — if not already added.

### 4.2 NP/IF metadata columns (per-prediction)

```sql
strategy_achieved_np_watts    double precision null,
strategy_achieved_if          double precision null,
strategy_scale_factor         double precision null,
strategy_ftp_watts            double precision null
```

### 4.3 Adjusted segments

Reuses `prediction_adjusted_segments` table from Strategy 1 (if built first) or introduces it here.

---

## 5. UX Flow

1. "Pacing strategy" panel → select "Normalized Power / IF Target".
2. FTP field: numeric input (W), with tooltip "Your Functional Threshold Power. Check your training app if unsure."
3. Target IF slider: 0.60 – 1.10 with labelled zones:
   - 0.60–0.75: Easy endurance
   - 0.75–0.85: Tempo
   - 0.85–0.95: Threshold
   - 0.95–1.05: VO₂ max
   - 1.05+: Anaerobic (short efforts only)
4. Scaling mode: radio "Proportional (recommended)" / "Additive (fixed watt offset)".
5. On submit: result page shows:
   - Predicted time (adjusted vs baseline)
   - Achieved NP and IF (post-hoc confirmation)
   - Scale factor applied (e.g. "+12% above model baseline")
   - Power distribution chart: adjusted vs baseline per segment

---

## 6. Edge Cases

| Case | Handling |
|------|----------|
| FTP not provided | Return 400 `np-if-ftp-required` |
| FTP ≤ 0 or > 2000 W | Return 400 `np-if-ftp-out-of-range` |
| Target IF ≤ 0 or > 1.5 | Return 400 `np-if-target-out-of-range` |
| Bisection does not converge in maxIterations | Return warning `np-if-convergence-failed`; fall back to closest S found |
| S < 0.5 (deep recovery effort) | Warn `np-if-very-low-intensity`; proceed |
| S > 2.0 (extreme effort) | Warn `np-if-very-high-intensity`; proceed (rider's choice) |
| Route too short for 30-second rolling window | NP degrades to mean power; warn `np-if-short-route-np-approximation` |
| Route segments have zero duration (bad GPX) | Pre-validated before strategy step; upstream validation handles |
| Adjusted watts fall below physics minimum (rider cannot sustain speed uphill) | Physics engine already handles: speed converges to near-zero, time expands; no crash |
| Additive mode produces negative watts for descent segments | Clamp to 0 W; warn `np-if-additive-clamped` |

---

## 7. Validation Approach

### Unit tests
- `NpIfScaler.ComputeNP` matches known hand-calculated NP for a synthetic constant-power ride
- `NpIfScaler.ComputeNP` matches known NP for a variable-power ride with gradient steps
- `NpIfScaler.Solve` converges to ±0.5 W for IF 0.85 with FTP 280 W on a real route fixture
- S = 1.0 when baseline NP already matches target NP
- Proportional vs additive modes produce distinct results

### Integration tests
- Full job pipeline with NP/IF strategy stores `strategy_achieved_np_watts` within 1 W of target
- Without strategy, existing predictions unaffected (regression)

### Back-testing validation
- For each historical ride, compute IF rider actually rode; check that applying that IF as the target recovers the actual time within 3 % (sanity check on the NP approximation accuracy).

---

## 8. Rollout Sequencing

| Phase | Work | Gate |
|-------|------|------|
| P0 | Domain types: `NpIfStrategy`, `NpIfScalingMode` | PR review |
| P0 | `NpIfScaler.ComputeNP` + unit tests | All unit tests green |
| P0 | `NpIfScaler.Solve` (bisection) + unit tests | All unit tests green |
| P1 | Contract extension for NP/IF strategy submission | PR review |
| P1 | DB migration: NP/IF metadata columns | Migration tested on staging |
| P1 | `PredictionSubmissionService` serialisation | Integration test |
| P2 | `PredictionJobHandler`: invoke `NpIfScaler`, re-run predictor with `ScaledPowerLookup` | Integration test |
| P2 | `PredictionPublication` extended; `PredictionQueryService` returns NP/IF metadata | API contract test |
| P3 | UI: FTP input, IF slider, zones, result overlay | Manual smoke test |
| P4 | Back-test validation suite against historical rides | ≤ 3 % time delta vs actuals |
| P4 | Feature flag removal / GA | Sign-off |

**Dependency on Strategy 1:** Shares `prediction_adjusted_segments` table and `strategy_type`/`strategy_json` columns. If Strategy 1 is built first, P1 migrations can be skipped (already exist).

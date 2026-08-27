# Strategy 5: Variable Match-Burning

**Date:** 2026-08-27  
**Status:** Plan only — no code changes

---

## 1. Purpose

"Match-burning" is a cycling term for a brief, intense anaerobic effort that exceeds the rider's sustainable threshold — a "match" burned from a finite reserve. This strategy models a rider who deliberately plans high-intensity surges on specific route sections (key climbs, sprint finishes, etc.) while conserving energy before and recovering after each surge.

The system:
1. Lets the rider designate one or more **burn windows** (by gradient range, route distance range, or explicit sequence range).
2. Assigns an intensity level (watt target or zone) to each burn window.
3. Automatically computes a **conservation phase** immediately before each burn and a **recovery phase** immediately after, reducing wattage in those windows to simulate realistic pre-loading and post-surge fatigue.
4. Simulates the combined pacing plan and reports predicted time, fatigue accumulation estimate, and whether the plan is physiologically coherent.

This is the most complex strategy: it requires a model of anaerobic capacity (W′ or a simplified approximation), which must be derived from the personal power model or supplied by the user.

---

## 2. Architecture

### 2.1 Conceptual model: W′ Balance

The W′ Balance model (Skiba et al.) tracks anaerobic capacity:

```
W′_balance(t) = W′ - integral[from 0 to t]( max(0, P(τ) - CP) dτ )
                    + integral (W′ recovery above CP)
```

Where:
- `W′` = anaerobic work capacity (joules); estimated or provided
- `CP` = critical power ≈ FTP × 0.93 (or provided directly)
- `P(τ)` = power at time τ

A "match" is a segment where `P > CP`. W′ balance must remain ≥ 0 at all times for the plan to be physiologically feasible.

For RouteTimer, CP and W′ can be:
- **Provided by the user** (most accurate)
- **Inferred from the power model**: CP ≈ model watt output at the `180:+` duration band (the longest duration band represents near-critical effort); W′ estimated from the model's difference between short-duration and CP power.

### 2.2 New types

```csharp
// RouteTimer.Domain/Predictions/PacingStrategies/
public sealed record MatchBurningStrategy(
    double? CriticalPowerWatts,   // null = infer from model
    double? WPrimeJoules,         // null = infer from model
    IReadOnlyList<BurnWindow> BurnWindows,
    ConservationPhaseConfig ConservationPhase,
    RecoveryPhaseConfig RecoveryPhase,
    bool IncludeFatigueReport);

public sealed record BurnWindow(
    BurnWindowTarget Target,      // GradientRange | DistanceRange | SequenceRange
    double? GradientMin,
    double? GradientMax,
    double? DistanceFromMetres,
    double? DistanceToMetres,
    int? SequenceFrom,
    int? SequenceTo,
    BurnIntensity Intensity,      // ZoneBased | AbsoluteWatts | PercentCP
    int? Zone,                    // for ZoneBased
    double? AbsoluteWatts,        // for AbsoluteWatts
    double? PercentCp);           // for PercentCP; > 1.0 = above CP

public enum BurnWindowTarget { GradientRange, DistanceRange, SequenceRange }
public enum BurnIntensity { ZoneBased, AbsoluteWatts, PercentCp }

public sealed record ConservationPhaseConfig(
    double DurationSeconds,       // seconds before each burn window
    double TargetPercentCp);      // e.g. 0.80 = 80% of CP during conservation

public sealed record RecoveryPhaseConfig(
    double DurationSeconds,       // seconds after each burn window
    double TargetPercentCp);      // e.g. 0.70 = 70% of CP during recovery

public sealed record FatigueReport(
    IReadOnlyList<WPrimeBalancePoint> WPrimeBalanceSeries,
    double MinWPrimeBalanceJoules,
    double WPrimeDepleted,         // fraction: 0 = none, 1 = fully depleted
    FatigueVerdict Verdict,
    IReadOnlyList<string> CriticalSegments);  // sequences where W′ < 20%

public enum FatigueVerdict { Manageable, Aggressive, Risky, Infeasible }
```

### 2.3 New services

```csharp
// RouteTimer.Services/Predictions/PacingStrategies/
public sealed class MatchBurningCpEstimator
{
    // Estimates CP from the personal power model.
    // Uses the "180:+" duration band's global typical watts (riding at near-CP for 3+ hours)
    // adjusted conservatively: CP ≈ 0.95 × band[grade="-1:1"][duration="180:+"].TypicalWatts
    // W′ estimated from: W′ ≈ (band["1:3"]["0:30"] - CP) × 900s (30-min area above CP)
    public (double CP, double WPrime) Estimate(PowerModel model);
}

public sealed class MatchBurningWPrimeTracker
{
    // Tracks W′ balance through a prediction result, applying Skiba W′Bal model.
    // Returns per-segment W′ balance and aggregate fatigue report.
    public FatigueReport Track(
        PredictionResult prediction,
        double criticalPower,
        double wPrime);
}

public sealed class MatchBurningPowerLookup
{
    // Wraps PowerLookup. For each segment:
    // - If in a BurnWindow → return burn intensity watts
    // - If in a conservation phase before a burn window → return conservation watts
    // - If in a recovery phase after a burn window → return recovery watts
    // - Otherwise → return baseline model watts
    //
    // Phase boundaries are computed from cumulative distance/time in a pre-pass over the route.
}
```

### 2.4 Contract changes

```json
{
  "strategy": {
    "type": "variable-match-burning",
    "criticalPowerWatts": null,
    "wPrimeJoules": null,
    "burnWindows": [
      {
        "target": "gradient-range",
        "gradientMin": 0.06,
        "intensity": "percent-cp",
        "percentCp": 1.20
      },
      {
        "target": "distance-range",
        "distanceFromMetres": 95000,
        "distanceToMetres": 100000,
        "intensity": "absolute-watts",
        "absoluteWatts": 420
      }
    ],
    "conservationPhase": { "durationSeconds": 120, "targetPercentCp": 0.80 },
    "recoveryPhase": { "durationSeconds": 300, "targetPercentCp": 0.70 },
    "includeFatigueReport": true,
    "includeBaseline": true
  }
}
```

`PredictionDetailResponse` gains:
- `StrategyFatigueReport?: FatigueReportResponse`
- `StrategyInferredCpWatts?: double`
- `StrategyInferredWPrimeJoules?: double`

Per-segment response gains:
- `StrategyPhase?: string`  (`"burn"` | `"conservation"` | `"recovery"` | `"baseline"`)
- `StrategyWPrimeBalanceJoules?: double`

---

## 3. Algorithm Steps

### 3.1 Pre-pass: phase window resolution

Before running the predictor, perform a single pass over `route.Samples` to assign each segment to a phase:

1. Tag every segment as `burn` if it falls within any `BurnWindow`.
2. For each `burn` segment, walk backwards in the segment list by distance/time to tag `conservation` segments (up to `ConservationPhase.DurationSeconds` of cumulative time from baseline prediction).
3. Walk forwards to tag `recovery` segments.
4. Overlapping phases: `burn` > `recovery` > `conservation` > `baseline` (priority order).

This pre-pass uses the **baseline prediction** timing to estimate phase lengths. An optional second iteration can be done with the adjusted timing if precision matters (one extra predictor run).

### 3.2 MatchBurningPowerLookup

For each segment:
```
switch phase:
  burn:         targetWatts = resolveBurnIntensity(window, CP)
  conservation: targetWatts = CP × conservationPhase.TargetPercentCp
  recovery:     targetWatts = CP × recoveryPhase.TargetPercentCp
  baseline:     targetWatts = powerLookup.GetWatts(gradient, elapsed).Watts
```

### 3.3 Predictor run

Pass `MatchBurningPowerLookup` into `RoutePredictor` as before. The result contains per-segment watts matching the burn plan.

### 3.4 W′ balance tracking

After the predictor run:
```
wBalance = W′
foreach segment in order:
  dt = segment.MovingTime.TotalSeconds
  P = segment.PowerWatts
  if P > CP:
    wBalance -= (P - CP) × dt          // spending anaerobic energy
  else:
    wBalance += (CP - P) × dt × recoveryRate  // recovering
    wBalance = min(wBalance, W′)        // can't exceed full W′
  record (sequence, wBalance)
  if wBalance < 0: wBalance = 0; flag infeasible
```

`recoveryRate` ≈ 0.5–0.7 (standard Skiba constant; configurable or fixed at 0.6).

### 3.5 Feasibility assessment

- `Manageable`: `MinWPrimeBalance ≥ 0.3 × W′`
- `Aggressive`: `0.1 × W′ ≤ MinWPrimeBalance < 0.3 × W′`
- `Risky`: `0 < MinWPrimeBalance < 0.1 × W′`
- `Infeasible`: W′ balance hits 0 at any point

---

## 4. Data Model Changes

### 4.1 Shared strategy columns

`strategy_type`, `strategy_json`.

### 4.2 Match-burning metadata

```sql
strategy_cp_watts              double precision null,
strategy_wprime_joules         double precision null,
strategy_inferred_cp           boolean null,
strategy_inferred_wprime       boolean null,
strategy_min_wprime_balance    double precision null,
strategy_fatigue_verdict       varchar(20) null,
strategy_fatigue_json          jsonb null
```

### 4.3 Adjusted segments (shared)

Add columns to `prediction_adjusted_segments`:
```sql
strategy_phase            varchar(20) null,   -- 'burn' | 'conservation' | 'recovery' | 'baseline'
strategy_wprime_balance   double precision null
```

---

## 5. UX Flow

1. "Pacing strategy" panel → "Variable Match-Burning".
2. CP/W′ section: toggle "Infer from my power model" (default on) or enter manually.
   - If inferred: show estimated values with "Based on your longest-effort power data" note.
3. Burn windows section: "Add a surge" button.
   - Each window: target type, target params, intensity selector (zone / absolute W / % CP).
   - Can reorder, duplicate, delete.
4. Conservation phase: duration slider (0–300 s), intensity slider (50–100 % CP).
5. Recovery phase: duration slider (0–600 s), intensity slider (50–90 % CP).
6. "Include fatigue report" checkbox (default on).
7. Result page:
   - Route map / elevation profile with colour-coded phases (burn = red, conservation = yellow, recovery = green, baseline = grey).
   - W′ balance chart over route distance.
   - Fatigue verdict banner (Manageable / Aggressive / Risky / Infeasible).
   - List of critical segments if W′ < 20 % threshold.
   - Baseline vs adjusted time.
   - Note if CP/W′ were inferred with a prompt to "Calibrate with a CP test".

---

## 6. Edge Cases

| Case | Handling |
|------|----------|
| No burn windows | Strategy is a no-op; return baseline; warn `match-burning-no-windows` |
| W′ = 0 from inference (model lacks short-effort data) | Set W′ to a population default (15 kJ); warn `match-burning-wprime-inferred-default` |
| CP inference yields CP > all band watts | Return 400 `match-burning-cp-inference-failed` with guidance |
| Burn window covers entire route | W′ balance immediately depleted; `Infeasible` verdict |
| Burn windows overlap | Union of overlapping windows treated as one continuous burn; warn `match-burning-overlapping-windows` |
| Conservation/recovery windows overlap with another burn window | Burn takes priority; conservation/recovery silently truncated |
| Phase pre-pass uses baseline timing but adjusted timing is very different | Offer optional second iteration (configurable; default off for performance) |
| PercentCp < 0.5 (very easy burn) | Warn `match-burning-burn-below-cp`; proceed (rider may want sub-CP "surge" for tactical reasons) |
| AbsoluteWatts burn intensity < 10 W | Reject: `match-burning-burn-watts-too-low` |
| AbsoluteWatts burn intensity > 2000 W | Reject: `match-burning-burn-watts-too-high` |
| Route has no segments matching gradient-range burn target | Warn `match-burning-window-no-match`; proceed (window inactive) |
| W′ balance goes negative mid-route | Clamp to 0, continue simulation, mark subsequent segments as `infeasible` in the report |

---

## 7. Validation Approach

### Unit tests
- `MatchBurningCpEstimator.Estimate` produces CP ≈ 0.95 × `band["-1:1"]["180:+"].TypicalWatts`
- `MatchBurningCpEstimator.Estimate` produces plausible W′ estimate
- `MatchBurningWPrimeTracker.Track` on constant power below CP → W′ balance unchanged
- `MatchBurningWPrimeTracker.Track` on power above CP → W′ decreases at correct rate
- W′ recovers at recovery rate below CP
- Phase pre-pass correctly identifies burn, conservation, recovery segments
- Overlapping windows take priority correctly

### Integration tests
- Full job with a single burn window: `strategy_phase = "burn"` for matching segments
- `strategy_wprime_balance` decreases during burn, recovers after
- Fatigue report stored in `strategy_fatigue_json`
- W′ depletion mid-route → `Infeasible` verdict stored correctly
- Without strategy: no regression

### Back-testing validation
- Select 5 historical rides with known high-intensity climbs (identifiable from power data). Apply a burn window matching those climb sections. Verify:
  - The adjusted prediction's climbing speed matches the actual climbing speed within 5 %.
  - The W′ balance model shows depletion consistent with the rider's actual subsequent power drop (qualitative check from training data).

---

## 8. Rollout Sequencing

| Phase | Work | Gate |
|-------|------|------|
| P0 | Domain types: `MatchBurningStrategy`, `BurnWindow`, `FatigueReport` + enums | PR review |
| P0 | `MatchBurningCpEstimator` + unit tests | All unit tests green |
| P0 | `MatchBurningWPrimeTracker` + unit tests | All unit tests green |
| P0 | Phase pre-pass logic (segment tagging) + unit tests | All unit tests green |
| P0 | `MatchBurningPowerLookup` + unit tests | All unit tests green |
| P1 | Contract extension: `MatchBurningStrategy`, fatigue report response types | PR review |
| P1 | DB migration: CP/W′ metadata columns; `strategy_phase`, `strategy_wprime_balance` on adjusted segments | Migration tested |
| P1 | `PredictionSubmissionService` serialisation | Integration test |
| P2 | `PredictionJobHandler`: invoke match-burning pipeline, publish fatigue report | Integration test |
| P2 | `PredictionQueryService` returns phase and W′ balance per segment, fatigue report summary | API contract test |
| P3 | UI: burn window builder, conservation/recovery sliders, W′ balance chart, phase map | Manual smoke test |
| P4 | Back-test validation | See §7 |
| P4 | Optional: second-iteration phase timing refinement | Toggle behind sub-flag |
| P4 | Feature flag removal / GA | Sign-off |

**Dependencies:**
- Strategies 1–4's shared infrastructure (strategy columns, adjusted segments table).
- `ZoneResolver` from Strategy 4 (reused for `ZoneBased` burn intensity).
- CP/W′ inference requires a rider model with evidence in both short-duration (`0:30`) and long-duration (`180:+`) bands; model confidence propagates into warnings.

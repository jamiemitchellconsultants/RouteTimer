# Strategy 1: Segment-Specific Gains

**Date:** 2026-08-27  
**Status:** Plan only — no code changes

---

## 1. Purpose

Allow a rider to specify a multiplier (or absolute watt delta) for one or more named route segments — e.g. "push 10 % harder on climbs above 6 %, ease off 8 % on the flat". The engine applies the overrides on top of the personal power model, re-runs the physics simulation, and returns an adjusted prediction alongside the baseline.

This is the simplest personalisation lever: it requires no new training data, no new model-building step, and does not alter the existing band grid.

---

## 2. Architecture

### 2.1 Placement in the pipeline

```
SubmitPrediction (API)
    ↓ PacingStrategy = SegmentSpecificGains { overrides: [...] }
PredictionSubmissionService
    ↓ stores strategy in QueuedPredictionCreation
PredictionJobHandler
    ↓ resolves SegmentGainsOverlay from strategy
RoutePredictor.Predict(route, profile, model, overlay?)
    ↓ PowerLookup.GetWatts(...)  ← multiplied by overlay factor for matching segments
PredictionResult (baseline) + PredictionResult (adjusted)
```

The existing `RoutePredictor` should remain unchanged. A new **`SegmentGainsApplicator`** wraps `PowerLookup` and intercepts each `GetWatts` call to apply the relevant factor for the current segment's gradient/characteristics.

### 2.2 New types (domain layer)

```csharp
// RouteTimer.Domain/Predictions/PacingStrategies/
public sealed record SegmentGainRule(
    GainRuleTarget Target,       // GradientRange | SequenceRange | DistanceRange
    double Factor,               // 1.0 = no change; 1.10 = 10% more watts
    double? AbsoluteDeltaWatts); // alternative to Factor; mutual exclusion validated

public enum GainRuleTarget { GradientRange, SequenceRange, DistanceRange }

public sealed record SegmentGainsStrategy(
    IReadOnlyList<SegmentGainRule> Rules,
    bool IncludeBaselineSide);   // whether to return both adjusted and unadjusted
```

### 2.3 New service (services layer)

```csharp
// RouteTimer.Services/Predictions/PacingStrategies/
public sealed class SegmentGainsPowerLookup : IPowerLookupSource
{
    // Wraps PowerLookup; for each GetWatts call, checks if the current RouteSample
    // falls within any rule's target range and applies the first matching rule's factor.
}
```

### 2.4 Contract changes

`PredictionSubmissionRequest` gains an optional `PacingStrategy` discriminated union field:

```json
{
  "strategy": {
    "type": "segment-specific-gains",
    "rules": [
      { "target": "gradient-range", "gradientMin": 0.06, "factor": 1.10 },
      { "target": "gradient-range", "gradientMax": -0.03, "factor": 0.95 }
    ],
    "includeBaseline": true
  }
}
```

`PredictionDetailResponse` gains optional `AdjustedSegments` and `AdjustedMovingSeconds`.

---

## 3. Algorithm Steps

1. **Submission** – client POSTs GPX + strategy JSON. `PredictionSubmissionService` serialises strategy into `PredictionAssumptions` (or a new sibling `PredictionStrategy` column).

2. **Job pickup** – `PredictionJobHandler` deserialises the strategy from the stored entity and constructs a `SegmentGainsPowerLookup` wrapping the standard `PowerLookup`.

3. **Segment loop** – for each route sample, `SegmentGainsPowerLookup.GetWatts(gradient, elapsed)`:
   a. Call underlying `PowerLookup.GetWatts` → `baseEstimate`.
   b. Iterate rules in order; take the **first** matching rule.
   c. Compute `adjustedWatts = baseEstimate.Watts × rule.Factor + rule.AbsoluteDeltaWatts ?? 0`.
   d. Clamp `adjustedWatts ≥ 0`.
   e. Return new `PowerEstimate(adjustedWatts, baseEstimate.Confidence, baseEstimate.Extrapolated, "segment-gain-adjusted")`.

4. **Parallel run** – if `IncludeBaselineSide`, the job handler runs the predictor twice: once with the overlay wrapper, once with the standard `PowerLookup`. Both results are stored and returned.

5. **Publication** – `PredictionPublication` extended to carry optional `AdjustedSegments`, `AdjustedMovingTime`, `AdjustedAveragePower`.

---

## 4. Data Model Changes

### 4.1 Strategy storage

Add `StrategyType varchar(50)` and `StrategyJson jsonb` columns to the `predictions` table (nullable; null = no strategy = existing baseline behaviour).

Migration: `AddColumn_PredictionStrategy_<timestamp>.sql`

### 4.2 Adjusted result columns

```sql
-- predictions table additions
adjusted_moving_seconds      double precision null,
adjusted_average_speed_mps   double precision null,
adjusted_average_power_watts double precision null
```

### 4.3 Adjusted segment storage

New table `prediction_adjusted_segments` mirroring `prediction_segments` structure but keyed by `prediction_id + sequence`. Populated only when an adjusted run was produced.

---

## 5. UX Flow

1. **Prediction submission form** gains a collapsible "Pacing strategy" panel, initially hidden.
2. User selects "Segment-specific gains" from a strategy picker.
3. A rule builder appears: target type dropdown, gradient min/max (or sequence/distance range), factor slider (50 % – 200 %) or watt input.
4. User adds N rules (cap at 10 for sanity). Rules shown as chips with edit/delete.
5. "Include baseline comparison" toggle (default on).
6. On submit, the form POSTs strategy JSON alongside the GPX.
7. Results page shows two overlaid speed/power curves if baseline comparison is enabled; a diff panel shows time saved/lost per segment.

---

## 6. Edge Cases

| Case | Handling |
|------|----------|
| No rules supplied | Strategy is a no-op; treat as baseline run |
| Overlapping rules | First matching rule wins (order matters; API validates and documents this) |
| Factor produces near-zero watts (< 10 W) | Clamp to 10 W; add warning `segment-gains-power-clamped` |
| Factor > 5.0 or < 0.1 | Reject at submission validation; return 400 |
| `AbsoluteDeltaWatts` and `Factor` both set | Return 400: `segment-gains-rule-ambiguous` |
| GradientMin > GradientMax | Return 400: `segment-gains-invalid-range` |
| SequenceRange references sequences outside route | Warn at runtime; silently skip non-matching segments (rule effectively inactive) |
| Adjusted result identical to baseline | Acceptable; no special treatment; diff panel shows zero delta |
| Long route where adjusted run doubles wall time | Background job already async; no extra concern |

---

## 7. Validation Approach

### Unit tests (RouteTimer.Domain / RouteTimer.Services)
- `SegmentGainsPowerLookup` returns base watts when no rule matches
- Factor of 1.1 on gradient range [0.06, ∞] applies correctly to matching segment, is a no-op on flat segment
- Clamp to 0 W applied when factor drives watts negative
- Duplicate-rule ordering: first rule wins

### Integration tests (RouteTimer.Tests / existing job test harness)
- End-to-end job with a strategy produces `AdjustedSegments` populated in the stored entity
- End-to-end job without a strategy is unaffected (regression)
- Baseline side stored correctly when `IncludeBaselineSide = true`

### API contract tests
- Invalid strategy JSON → 400
- Missing GPX → 400
- Valid strategy accepted → 202 + expected `PredictionSubmissionResponse`

### Validation metric reuse
- Run existing validation service against the adjusted segments (cross-check back-testing error vs baseline; should not degrade by more than a configurable threshold, e.g. 5 percentage points in MAPE).

---

## 8. Rollout Sequencing

| Phase | Work | Gate |
|-------|------|------|
| P0 | Domain types: `SegmentGainRule`, `SegmentGainsStrategy`, `GainRuleTarget` | PR review |
| P0 | `SegmentGainsPowerLookup` + unit tests | All unit tests green |
| P1 | Contract extension: `PacingStrategyRequest` discriminated union in `PredictionContracts.cs` | PR review |
| P1 | DB migration: `strategy_type`, `strategy_json` columns | Migration tested on staging |
| P1 | `PredictionSubmissionService` serialisation | Integration test |
| P2 | `PredictionJobHandler` deserialisation + dual-run logic | Integration test |
| P2 | `PredictionPublication` / `PersistedPredictionSegment` extension for adjusted path | Unit + integration tests |
| P2 | `PredictionQueryService` returns adjusted segments in `PredictionDetailResponse` | API contract test |
| P3 | DB migration: `adjusted_moving_seconds`, `adjusted_average_power_watts` columns + `prediction_adjusted_segments` table | Migration tested |
| P3 | UI: strategy picker, rule builder, comparison overlay | Manual smoke test |
| P4 | End-to-end validation against historical rides; compare MAPE delta | ≤ 5 pp MAPE regression allowed |
| P4 | Feature flag removal / GA | Sign-off |

**Feature flag:** `pacing-strategy-enabled` (off by default in production until P4).

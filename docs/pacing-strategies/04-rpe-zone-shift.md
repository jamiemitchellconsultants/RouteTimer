# Strategy 4: RPE / Zone Shift

**Date:** 2026-08-27  
**Status:** Plan only — no code changes

---

## 1. Purpose

Rate of Perceived Exertion (RPE) and training zones both describe effort in a rider-centric (rather than watt-centric) language. This strategy lets a rider say "I want this whole ride to feel like a Zone 2 effort" or "shift my power targets up one zone for the climbs and back to Zone 2 on the flats". The system:

1. Resolves watt boundaries for each zone from the rider's personal power model (using FTP or CP if provided, or inferring zone boundaries from the model's gradient/duration data).
2. Maps the rider's requested zone(s) to a target power range per segment type.
3. Scales the model's per-segment watts to land within the requested zone boundary.
4. Runs the physics simulation and returns both a zoned prediction and a physiological effort summary.

The personal power model already encodes typical watts by gradient and duration. Zones add a **relative-effort layer** on top, anchored to the rider's own history rather than generic population norms.

---

## 2. Architecture

### 2.1 Zone definition

Two zone schemes are supported:

**FTP-based (Coggan 7-zone):**
| Zone | Name | Lower % FTP | Upper % FTP |
|------|------|-------------|-------------|
| Z1 | Recovery | 0 | 55 |
| Z2 | Endurance | 55 | 75 |
| Z3 | Tempo | 75 | 90 |
| Z4 | Threshold | 90 | 105 |
| Z5 | VO₂ max | 105 | 120 |
| Z6 | Anaerobic | 120 | 150 |
| Z7 | Neuromuscular | 150 | ∞ |

**Model-inferred (no FTP required):**
Use the personal power model's global median as a proxy for "moderate" (≈ Z3) and scale zone boundaries relative to it. Less accurate but usable when FTP is unknown.

### 2.2 New types

```csharp
// RouteTimer.Domain/Predictions/PacingStrategies/
public sealed record RpeZoneStrategy(
    ZoneScheme Scheme,           // FtpBased | ModelInferred
    double? FtpWatts,            // required for FtpBased scheme
    IReadOnlyList<ZoneAssignment> Assignments,
    bool IncludeZoneDistributionReport);

public sealed record ZoneAssignment(
    ZoneAssignmentTarget Target,  // AllSegments | GradientRange
    double? GradientMin,
    double? GradientMax,
    int Zone,                     // 1–7 for FtpBased, 1–5 for ModelInferred
    ZonePlacement Placement);     // Midpoint | LowerBound | UpperBound

public enum ZoneScheme { FtpBased, ModelInferred }
public enum ZoneAssignmentTarget { AllSegments, GradientRange }
public enum ZonePlacement { Midpoint, LowerBound, UpperBound }

public sealed record ZoneDistributionReport(
    IReadOnlyList<ZoneBandSummary> ByZone,
    double PercentTimeInZone,    // for the requested zone (if single-zone strategy)
    double AverageWatts,
    double NormalizedPowerWatts);

public sealed record ZoneBandSummary(
    int Zone, string ZoneName,
    double LowerWatts, double UpperWatts,
    double PercentTime, TimeSpan TimeInZone);
```

### 2.3 New service

```csharp
// RouteTimer.Services/Predictions/PacingStrategies/
public sealed class RpeZoneScaler
{
    // Resolves the watt target for each route segment given the zone assignments.
    // For each segment: find matching assignment → zone → watt range → target watts
    // (midpoint, lower, or upper of zone based on ZonePlacement).
    public ScaledPowerLookup BuildScaledLookup(
        PowerLookup baseline,
        RpeZoneStrategy strategy,
        ZoneResolver resolver);

    // Computes zone distribution from an adjusted PredictionResult.
    public ZoneDistributionReport BuildDistributionReport(
        PredictionResult adjustedResult,
        ZoneResolver resolver);
}

public sealed class ZoneResolver
{
    // Given a ZoneScheme and optional FTP, returns the watt range for a given zone number.
    public ZoneRange Resolve(int zone, ZoneScheme scheme, double? ftpWatts, PowerModel model);

    // Returns the zone number for a given watt value.
    public int WattsToZone(double watts, ZoneScheme scheme, double? ftpWatts, PowerModel model);
}
```

### 2.4 Contract changes

```json
{
  "strategy": {
    "type": "rpe-zone-shift",
    "scheme": "ftp-based",
    "ftpWatts": 280,
    "assignments": [
      { "target": "all-segments", "zone": 2 },
      { "target": "gradient-range", "gradientMin": 0.03, "zone": 3, "placement": "midpoint" }
    ],
    "includeZoneDistributionReport": true,
    "includeBaseline": true
  }
}
```

`PredictionDetailResponse` gains:
- `StrategyZoneDistribution?: ZoneDistributionReportResponse`

---

## 3. Algorithm Steps

### 3.1 Zone boundary resolution

**FTP-based:**
```
For zone Z with FTP = F:
  lowerWatts = F × lowerPercentFtp[Z] / 100
  upperWatts = F × upperPercentFtp[Z] / 100
  midpointWatts = (lowerWatts + upperWatts) / 2
```

**Model-inferred:**
```
globalMedianWatts = model.PowerModel.GlobalTypicalWatts  (≈ Z3 midpoint proxy)
Scale zone boundaries relative to globalMedianWatts:
  Z2 midpoint ≈ 0.65 × globalMedian (using same % relationships as FTP-based)
  Etc.
```

### 3.2 Per-segment target resolution

For each route segment (at gradient g, elapsed duration t):
1. Identify matching `ZoneAssignment` (first match wins, ordered by specificity: `GradientRange` before `AllSegments`).
2. Resolve zone watt range from `ZoneResolver`.
3. Compute target watts from placement:
   - `Midpoint`: `(lower + upper) / 2`
   - `LowerBound`: `lower + 5 W` (small offset to be clearly inside zone)
   - `UpperBound`: `upper - 5 W`
4. Compute scale factor for this segment: `S_seg = targetWatts / baseline.Watts`
5. Apply: `adjustedWatts = baseline.Watts × S_seg` (clamped ≥ 0)

This produces a **per-segment** (not global) scale, unlike Strategies 2 and 3.

### 3.3 Re-run predictor

Pass a `ZoneScaledPowerLookup` — which returns the segment-specific target watts from step 3.2 — into `RoutePredictor`. The lookup is keyed on gradient band (matching how the strategy assignments are specified).

### 3.4 Zone distribution report

After the adjusted run:
- For each segment, call `ZoneResolver.WattsToZone(adjustedWatts)`.
- Aggregate time-in-zone.
- Return `ZoneDistributionReport`.

---

## 4. Data Model Changes

### 4.1 Shared strategy columns (from Strategy 1/2)

`strategy_type`, `strategy_json`.

### 4.2 Zone-specific metadata

```sql
strategy_zone_scheme         varchar(20) null,   -- 'ftp-based' | 'model-inferred'
strategy_ftp_watts           double precision null,
strategy_zone_distribution   jsonb null           -- ZoneDistributionReport JSON
```

### 4.3 Adjusted segments (shared `prediction_adjusted_segments`)

Per-segment: consider adding `zone_number int null` column so the UI can colour-code segments by zone.

---

## 5. UX Flow

1. "Pacing strategy" panel → "RPE / Zone Shift".
2. Zone scheme picker: "FTP-based (Coggan)" / "Use my power model (no FTP needed)".
3. If FTP-based: FTP input (W).
4. Zone assignment builder:
   - Default: single "all segments" row with zone picker (Z1–Z7 with colour swatches and labels).
   - "+ Add zone override" to add a gradient-range-specific row (e.g. "On climbs above 6 %: Zone 3").
   - Placement: "Mid-zone (recommended)" / "Zone floor" / "Zone ceiling".
5. "Include zone distribution report" checkbox (default on).
6. Result page:
   - Stacked bar: % time in each zone.
   - Segment map coloured by zone.
   - Baseline vs adjusted time.
   - If model-inferred zones used: disclaimer banner.

---

## 6. Edge Cases

| Case | Handling |
|------|----------|
| FTP not provided for FTP-based scheme | Return 400 `rpe-zone-ftp-required` |
| Zone number out of range (< 1 or > 7 for FTP-based) | Return 400 `rpe-zone-invalid-zone` |
| No assignments provided | Return 400 `rpe-zone-no-assignments` |
| Overlapping gradient-range assignments | First match wins; document and warn `rpe-zone-overlapping-assignments` |
| Zone 7 upper bound is infinite | Use UpperBound placement = `baseline.Watts × 1.5` as practical cap; warn `rpe-zone-z7-capped` |
| Model-inferred zones when model has very low confidence | Add `rpe-zone-model-low-confidence` warning; proceed |
| Requested zone target is far below physics minimum speed on a steep climb | Physics engine handles: time expands; watts are honoured |
| Multiple "all-segments" assignments | Reject at validation: `rpe-zone-duplicate-all-segments` |
| Zone shift makes descent slower than descent speed cap | Descent speed cap still applies (unchanged); combined result may be slower than zone target alone implies |

---

## 7. Validation Approach

### Unit tests
- `ZoneResolver.Resolve` returns correct watt ranges for all 7 zones at FTP = 300 W
- `ZoneResolver.Resolve` for model-inferred scheme returns plausible ranges relative to `GlobalTypicalWatts`
- `RpeZoneScaler.BuildScaledLookup`: Z2 midpoint assignment on flat segment returns correct adjusted watts
- Zone-specific gradient override takes precedence over `AllSegments` assignment
- `RpeZoneScaler.BuildDistributionReport`: all segments in Z2 produces 100% Z2 report
- Mixed zone assignment produces correct distribution split

### Integration tests
- Full job: Z2 strategy → adjusted prediction has NP in Z2 range (within ±5 W)
- Zone distribution report stored in `strategy_zone_distribution` column
- Without strategy: no regression

### Back-testing validation
- Apply Z2 strategy to 10 easy historical rides (low actual NP); verify the distribution report shows ≥ 70 % Z2 time (i.e. the strategy correctly identified what zone those rides were in).
- Apply Z4 strategy to 5 threshold historical rides; verify distribution shows ≥ 60 % Z4/Z5 time.

---

## 8. Rollout Sequencing

| Phase | Work | Gate |
|-------|------|------|
| P0 | Domain types: `RpeZoneStrategy`, `ZoneAssignment`, `ZoneDistributionReport`, `ZoneScheme` | PR review |
| P0 | `ZoneResolver` (FTP-based) + unit tests | All unit tests green |
| P0 | `ZoneResolver` (model-inferred) + unit tests | All unit tests green |
| P0 | `RpeZoneScaler.BuildScaledLookup` + unit tests | All unit tests green |
| P0 | `RpeZoneScaler.BuildDistributionReport` + unit tests | All unit tests green |
| P1 | Contract extension | PR review |
| P1 | DB migration: zone metadata columns; `zone_number` column on `prediction_adjusted_segments` | Migration tested |
| P1 | `PredictionSubmissionService` serialisation | Integration test |
| P2 | `PredictionJobHandler` integration | Integration test |
| P2 | `PredictionQueryService` returns zone distribution | API contract test |
| P3 | UI: scheme picker, zone assignment builder, distribution chart, segment map colours | Manual smoke test |
| P4 | Back-test validation | See §7 |
| P4 | Feature flag removal / GA | Sign-off |

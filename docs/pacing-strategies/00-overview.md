# RouteTimer Pacing Strategy Plans — Overview

**Session:** Pacing strategy plans  
**Branch:** jamiemitchellconsultants-pacing-strategy-plans  
**Date:** 2026-08-27  
**Status:** Plans only — no code changes made

## Strategies Planned

| # | Strategy | Document |
|---|----------|----------|
| 1 | Segment-Specific Gains | `01-segment-specific-gains.md` |
| 2 | Normalized Power / IF Target | `02-normalized-power-if-target.md` |
| 3 | Time Target Mode | `03-time-target-mode.md` |
| 4 | RPE / Zone Shift | `04-rpe-zone-shift.md` |
| 5 | Variable Match-Burning | `05-variable-match-burning.md` |

## Architecture Snapshot (for plan context)

### Core prediction pipeline

```
GPX upload → RouteProcessor → ProcessedRoute
                                      ↓
PredictionJobHandler → RoutePredictor.Predict(route, RiderProfile, RiderModel)
                                      ↓
                               PredictionResult
                          (segments: gradient, watts, speed, time, confidence)
```

### Key types

- **`RiderModel`** – `PowerModel` (8 gradient × 5 duration band grid of `PowerBand`), `PhysicalCoefficients`, `DescentLimitModel`, `WasCalibrated`
- **`PowerLookup`** – bilinear interpolation over the band grid; returns `PowerEstimate` (Watts, Confidence, Extrapolated)
- **`RiderProfile`** – `RiderWeightKg`, `BikeAndEquipmentWeightKg`
- **`PredictionAssumptions`** – Surface, Wind, Weather, MovingOnly (currently hard-coded to `RoadCalmDryMovingOnly`)
- **`PredictionSegment`** – Sequence, DistanceMetres, Gradient, PowerWatts, SpeedMetresPerSecond, MovingTime, Confidence
- **`PredictionResult`** – Segments, MovingTime, Confidence, Warnings

### Power model bands

- **Gradient keys:** `-100:-6`, `-6:-3`, `-3:-1`, `-1:1`, `1:3`, `3:6`, `6:9`, `9:100`
- **Duration keys:** `0:30`, `30:60`, `60:120`, `120:180`, `180:+` (minutes elapsed)

### Physics

`RoutePredictor` uses a sub-step Euler integrator. For each route segment it calls `PowerLookup.GetWatts(gradient, elapsed)` → watts, then resolves forces:

```
F_drive = watts × drivetrainEfficiency / speed
F_gravity = mass × g × sin(atan(gradient))
F_rolling = Crr × mass × g × cos(atan(gradient))
F_aero = 0.5 × CdA × airDensity × speed²
acceleration = (F_drive - F_resist) / mass
```

### Persistence / API boundaries

- Submission: `POST /api/predictions` (multipart GPX)
- Results: `GET /api/predictions/{id}` → `PredictionDetailResponse` (includes per-segment watts, speed, cumulative seconds)
- `PredictionAssumptions` is stored on the `PredictionEntity` in the database
- `QueuedPredictionCreation` carries the model snapshot, profile, and assumptions at time of submission

---

All five strategies add a **pacing strategy overlay** on top of the existing power-from-history baseline. None requires rewriting the physics core; they modify **what watts are fed into `RoutePredictor`** or how the result is post-processed.

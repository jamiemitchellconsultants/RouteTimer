# Route Simulator Step 8 Design

## Purpose

Step 8 upgrades RouteTimer's equilibrium-speed approximation to the approved calibrated, sequential cycling simulation. The model must learn bounded physical coefficients and typical descent/braking limits from eligible training activities, persist those values with each immutable rider-model version, and produce deterministic finite predictions with conservative fallbacks.

This design supplements `docs/superpowers/specs/2026-08-24-route-timer-design.md`. That document remains authoritative when the two overlap.

## Scope

This step includes:

- shared gradient and curvature enrichment for training evidence, including curvature persistence;
- robust bounded fitting of CdA and rolling resistance from steady riding evidence;
- learned descent-speed limits by grade and curvature, with conservative fallbacks;
- immutable persistence and round-tripping of descent limits and their coverage metadata;
- sequential route simulation with numerical substeps no longer than one simulated second;
- calibration/descent warning and confidence propagation into durable prediction results; and
- calibration within leave-one-activity-out validation folds so held-out rides do not leak into their own predictions.

This step does not add new UI, map/chart behavior, deployment work, or new user-configurable physics settings. Those remain in steps 9 and 10.

## Architecture

The simulator is split into focused components:

- `TrainingGeometryEnricher` derives smoothed gradient and curvature for cleaned activity samples using the same distance-based geometry rules as route processing.
- `PhysicsCalibrator` selects steady evidence and robustly fits bounded `Crr` and `CdA` values.
- `DescentLimitBuilder` learns effective speed caps for grade/curvature cells and supplies conservative fallback cells where evidence is insufficient.
- `DescentSpeedLimiter` resolves the effective cap and confidence for a predicted route segment.
- `RoutePredictor` performs sequential kinetic-energy integration and aggregates confidence/warning reasons.

`BuildModelJobHandler` coordinates these components and saves a new immutable rider model. `ModelValidator` builds calibration and descent limits from each fold's training activities only. The persistence layer stores descent cells in normalized rows linked to the rider-model version.

## Training Geometry Evidence

`CleanRideSample` gains `CurvaturePerMetre`. `ActivitySampleEntity` and its mapping gain the same field, with a migration adding a non-null column defaulted to zero for existing rows.

The cleaner marks the first retained sample after a timestamp gap as `CrossesDiscontinuity=true`; it does not silently reconnect the section after dropping a boundary sample. The geometry enricher operates independently on each continuous section, rejects non-finite coordinates/elevation, and never derives across that marker. It uses cumulative Haversine distance, a shared 100-metre robust local elevation fit, central gradient differences, and antimeridian-safe heading change per metre. Route processing is refactored to use the same shared fit rather than maintaining a second implementation. The first and last sample of a section use one-sided gradient windows and zero curvature. Gradient remains available for display outside ±20%, but evidence outside that range is excluded from fitting.

This prerequisite is deliberately narrow: it enriches existing cleaned activity samples without changing upload acceptance, moving-time selection, eligibility thresholds, or the persisted sequence/timing contract.

## Physical Calibration

`IPhysicsCalibrator.Calibrate(RiderProfile, IReadOnlyList<CleanedActivity>)` returns a `PhysicalCalibrationResult` containing coefficients, `WasCalibrated`, and a stable reason code.

Candidate intervals must:

- come from eligible activities and remain within one continuous section;
- have finite speed, gradient, timestamps, position, and power;
- have a duration greater than zero and no more than 10 seconds;
- have speed from 3 through 20 m/s and recorded power from 1 through 2,000 W;
- have gradient from -2% through +20%; and
- have absolute longitudinal acceleration no greater than 0.30 m/s².

These rules exclude stops, braking/coasting descents, implausible gradients, abrupt acceleration, missing power, and discontinuities. Calibration requires at least 60 intervals, 10 minutes of evidence, two distinct activities, speed standard deviation of at least 1 m/s, and gradient range of at least two percentage points.

For each interval, the linearized wheel-force balance is:

`Pwheel / speed - gravity force - inertial force = rolling basis × Crr + aerodynamic basis × CdA`

The fitter uses deterministic iteratively reweighted least squares with Huber residual weights. Every solve is bounded to `Crr=0.002..0.012` and `CdA=0.15..0.60 m²`. It rejects singular or poorly conditioned evidence, non-finite coefficients, and a robust residual objective worse than the default coefficients. Rejected calibration returns `PhysicalCoefficients.Default`, `WasCalibrated=false`, and one of `insufficient-physics-evidence`, `ill-conditioned-physics-fit`, or `physics-fit-not-improved`. Accepted calibration returns `WasCalibrated=true` and `physics-calibrated`.

Drivetrain efficiency remains 0.97 and air density remains 1.225 kg/m³ in this release; only Crr and CdA are fitted.

## Descent Limits

`DescentLimitModel` contains immutable `DescentLimitCell` values keyed by grade and curvature bands. Each cell stores its effective speed cap, evidence duration, distinct activity count, confidence, and whether it is learned or fallback.

Descending evidence requires an eligible activity, continuity, finite geometry/speed, speed of at least 2 m/s, and gradient at or below -2%. Cells use these bands:

- grade: mild `[-4%,-2%]`, medium `[-8%,-4%)`, and steep `(<-8%)`;
- curvature: straight `[0,0.002) 1/m`, moderate `[0.002,0.01) 1/m`, and tight `[0.01,+∞) 1/m`.

For a cell with at least five minutes of evidence from two activities, the learned cap is the deterministic 90th-percentile observed speed, clamped between 2 m/s and 20 m/s. It is shrunk toward the conservative cap until the cell reaches 20 minutes and three activities. High confidence requires 20 minutes and three activities; medium requires five minutes and two activities; all other cells use fallback limits with low confidence.

The conservative cap is the minimum of:

- the absolute 20 m/s cap;
- grade caps of 13 m/s for mild, 16 m/s for medium, and 18 m/s for steep descents; and
- `sqrt(2.0 / curvature)` when curvature is positive, representing a conservative 2.0 m/s² lateral-acceleration limit.

Missing or out-of-grid evidence resolves to the conservative formula and adds `conservative-descent-limits`. A learned cell may never exceed the absolute or curvature cap.

## Immutable Persistence

The rider-model schema gains:

- `DescentWasLearned` on `rider_models`; and
- `rider_model_descent_limits` with `(ModelId, GradeKey, CurvatureKey)` as the key plus speed cap, evidence seconds, activity count, confidence, and fallback flag.

`RiderModel` owns the `DescentLimitModel`; `RiderModelSnapshot` exposes whether learned descent evidence was available. `IRiderModelRepository.SaveAsync`, the EF mappings, migrations, and round-trip projections persist the complete immutable model in one transaction. Existing models migrate with conservative fallback descent limits and `DescentWasLearned=false`; historical coefficients and prediction snapshots remain unchanged.

## Sequential Simulation

The prediction starts at 0.5 m/s and carries terminal speed from one route segment into the next. For each segment, `PowerLookup` selects rider power from that segment's gradient and cumulative predicted moving time.

The segment is advanced using adaptive distance substeps. For each proposed distance, driving force is `wheel power / max(entry speed, 0.5 m/s)` and acceleration is `(driving force - gravity force - rolling force - aerodynamic force) / mass`. Exit speed follows `v² = u² + 2as`; elapsed time is `2s / (u + v)`. If the time is greater than one second, the proposed distance is halved and retried until it satisfies the bound. For each accepted substep the simulator:

1. computes wheel power after drivetrain efficiency;
2. computes gravity, rolling, and aerodynamic resistance;
3. advances kinetic energy and derives the next speed from the force balance;
4. applies the resolved descent cap; and
5. accumulates time from the average entry/exit speed.

The calculation uses a 0.5 m/s numerical floor only while converting power to force; stored predicted speed is the physical finite result after safeguards. A segment must consume positive distance and positive finite time. The simulator throws `PredictionCalculationException` if coefficients, route geometry, power, energy, speed, duration, or cumulative time are non-finite or negative, if convergence cannot advance, or if a bounded iteration limit is exceeded.

Predicted segment power remains the selected typical rider power. Average power continues to be time-weighted by the durable prediction workflow.

## Confidence and Warnings

Each segment's confidence is the minimum of power-cell confidence, descent-cell confidence when a descent cap applies, and physical-calibration confidence. Default physical coefficients force route confidence low. Conservative descent limits lower affected segment confidence and add `conservative-descent-limits` once per result. Power extrapolation adds `power-model-extrapolation`.

Route confidence follows the approved time-weighted rule:

- high only when calibrated coefficients are used and at least 80% of predicted moving time is high confidence;
- medium when at least 80% is medium-or-better; and
- low otherwise or whenever default coefficients dominate.

`PredictionResult` carries stable warning codes. `PredictionJobHandler` merges these with model-validation warnings before durable publication without duplicates.

## Validation Isolation

Leave-one-activity-out validation must not use the held-out activity for calibration or descent learning. For every fold, `ModelValidator` builds the fold power model, calibrates coefficients from the remaining activities, builds descent limits from the remaining activities, then predicts the held-out route. The final full model uses all eligible activities.

If a fold lacks calibration or descent evidence, it uses the same conservative fallbacks as production. This lowers confidence but does not prevent a finite moving-time comparison.

## Error Handling

Insufficient or poorly conditioned training evidence is an expected fallback, not a model-build failure. Invalid persisted model values are rejected when reconstructed. Prediction-time physical failures are permanent `PredictionJobException` failures through the existing handler/queue path and never publish partial segments.

All external diagnostics use stable safe codes. Raw residuals, activity contents, stack traces, and rider data remain in server-side logs or memory and are not exposed through API responses.

## Testing

The implementation uses test-first development and includes:

- known force-balance cases including kinetic-energy terms;
- synthetic recovery of known Crr/CdA values, coefficient bounds, order independence, and each fallback reason;
- geometry enrichment across normal sections and discontinuities, plus curvature persistence;
- learned, shrunk, fallback, curvature-limited, and absolute descent caps;
- sequential acceleration, deceleration, segment-to-segment speed continuity, cumulative-time power selection, and deterministic output;
- finite/non-negative property-style theories over grades, powers, masses, curvature, and segment lengths;
- warning deduplication and the 80%-of-time confidence thresholds;
- validation tests proving the held-out activity is excluded from calibration/descent learning; and
- PostgreSQL migration and repository round-trip tests for descent cells and backward-compatible existing models.

Focused service and persistence suites run during development. Before each commit, the full command is:

`dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 /nodeReuse:false -tl:off -v:minimal`

Formatting is verified with `dotnet format RouteTimer.slnx --no-restore --verify-no-changes --severity error`, followed by `git diff --check`.

## Acceptance Criteria

- Model construction either persists bounded calibrated Crr/CdA values or explicit safe fallback metadata.
- Learned descent cells are immutable, persisted, and used only when their evidence thresholds are met.
- Validation folds use only their training subset for power, physics, and descent learning.
- Prediction speed and time evolve sequentially with substeps no longer than one simulated second.
- Conservative grade/curvature limits protect uncovered descents and appear as stable warnings.
- Every published value is finite and non-negative; impossible simulations fail without partial publication.
- Existing prediction history and pre-step-8 rider models remain readable after migration.
- Focused tests, PostgreSQL integration tests, the full solution suite, formatting, and diff checks pass.

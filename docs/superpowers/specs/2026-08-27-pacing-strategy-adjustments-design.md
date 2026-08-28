# Pacing Strategy Adjustments Design

**Date:** 2026-08-27
**Status:** Approved

## Purpose

RouteTimer shall let a rider preserve a completed route prediction as an immutable baseline and
create any number of secondary pacing adjustments against it. Each adjustment applies exactly one
of the five strategies described under `docs/pacing-strategies/`, runs asynchronously, and can be
revisited or compared without changing the baseline or another adjustment.

This specification supersedes the strategy-at-submission and single-adjusted-result architecture in
those planning documents. Their product requirements remain in scope unless this specification
explicitly resolves an ambiguity or records an exclusion.

## Confirmed Product Decisions

- `POST /api/predictions` remains baseline-only.
- The existing prediction summary, segments, GPX exports, Garmin course actions, and history remain
  the primary result.
- A succeeded baseline may have multiple append-only adjustment children.
- Every adjustment contains exactly one pacing strategy. Strategies are never composed.
- Adjustments use the baseline's captured route, rider model, profile, and assumptions, not the
  rider's current model.
- Existing succeeded predictions are eligible for adjustments.
- One adjustment may be compared with the baseline at a time.
- Existing baseline rows are never modified by adjustment creation, execution, failure, or deletion.

## Scope

The feature shall provide:

1. shared adjustment persistence, jobs, contracts, APIs, simulation policies, feature flags, and
   comparison UI;
2. segment-specific gains;
3. normalized-power / intensity-factor targets;
4. time targets with proportional and climb-focused distribution;
5. FTP-based and model-inferred zone targets; and
6. variable match-burning with CP/W-prime estimates and a fatigue report.

The feature shall not provide:

- strategy selection during route submission;
- more than one strategy in an adjustment;
- mutation of a completed adjustment;
- adjustment creation before the baseline succeeds;
- adjusted GPX or Garmin export;
- rider-model rebuilding or recalibration;
- the undefined `EvenEffort` time-target mode from the earlier draft; or
- medical, physiological, or coaching advice.

## Existing Architecture

The current baseline flow is:

```text
POST /api/predictions
  -> PredictionSubmissionService
  -> PredictionRepository.CreateQueuedAsync
  -> PredictRoute analysis job
  -> PredictionJobHandler
  -> RoutePredictor
  -> PredictionRepository.TryPublishAsync
  -> GET /api/predictions/{id}
```

`RoutePredictor` currently constructs a concrete `PowerLookup` internally and asks it for one power
estimate at the start of every processed route segment. `prediction_segments` already persist the
complete input needed to simulate each segment: sequence, distance, cumulative distance, gradient,
curvature, and geometry. The leading `RouteSample` currently skipped by `RoutePredictor` is a parser
representation detail, not a simulated segment.

## Architecture

### Resource hierarchy

The existing `PredictionEntity` remains the baseline aggregate root. A new
`PredictionAdjustmentEntity` is a child of one baseline and owns its adjusted segments.

```text
Prediction baseline
  |-- immutable baseline summary and segments
  |-- Adjustment A: time target
  |     `-- adjusted summary, report, warnings, segments
  |-- Adjustment B: NP / IF target
  |     `-- adjusted summary, report, warnings, segments
  `-- Adjustment C: segment gains
        `-- adjusted summary, report, warnings, segments
```

Creating a new adjustment always inserts a sibling. No operation replaces another adjustment.
Deleting the baseline cancels active adjustment jobs and cascades through all adjustment data.
Deleting an adjustment affects only that child.

### Adjustment flow

```text
POST /api/predictions/{baselineId}/adjustments
  -> validate baseline, feature flag, discriminator, and strategy fields
  -> canonicalize and persist strategy definition
  -> enqueue AdjustPrediction job with adjustment id as SubjectId
  -> PredictionAdjustmentJobHandler
       -> load baseline summary and ordered segments
       -> load baseline RiderModel by captured RiderModelId
       -> reconstruct PredictionRoute from stored baseline segments
       -> dispatch one IPacingStrategyHandler
       -> run one or more full RoutePredictor simulations
       -> publish typed report and adjusted segments transactionally
```

### Component boundaries

`RouteTimer.Contracts` owns JSON request and response records. The request root uses
`JsonPolymorphic` and stable type discriminators. API endpoint mappers translate contracts into
domain strategy definitions; the services project does not take a dependency on the contracts
project.

`RouteTimer.Domain` owns immutable strategy definitions, typed strategy reports, route-segment
simulation inputs, adjustment state, warning codes, and per-segment annotations.

`RouteTimer.Services` owns request validation, canonical strategy serialization, strategy dispatch,
search, power-target policies, strategy algorithms, adjustment job orchestration, and result
validation.

`RouteTimer.Persistence` stores opaque canonical strategy/result JSON supplied by services, maps
adjustment entities, and provides owner-guarded creation/publication/deletion transactions. It does
not interpret strategy-specific fields.

`RouteTimer.Api` owns contract/domain mapping, feature-flag configuration, Problem Details mapping,
and the nested HTTP endpoints.

`RouteTimer.Client` owns strategy editors, job polling, adjustment selection, baseline comparison,
and typed report presentation.

## Simulation Refactor

### Route input

Introduce a route representation containing only simulated segments:

```csharp
public sealed record PredictionRoute(
    IReadOnlyList<PredictionRouteSegment> Segments,
    double DistanceMetres,
    double AscentMetres);

public sealed record PredictionRouteSegment(
    int Sequence,
    double Latitude,
    double Longitude,
    double ElevationMetres,
    double CumulativeDistanceMetres,
    double SegmentDistanceMetres,
    double Gradient,
    double CurvaturePerMetre);
```

The baseline job maps `ProcessedRoute.Samples.Skip(1)` into `PredictionRoute`. An adjustment maps
ordered `PersistedPredictionSegment` rows into the same type. This makes pre-feature predictions
adjustable without reparsing their retained GPX and removes the predictor's dependence on an unused
leading sample.

The refactor must preserve every current baseline result bit-for-bit at the public record boundary:
sequence, distance, gradient, power, speed, duration, confidence, warning order, and totals.

### Power-target policy

`IRoutePredictor.Predict` gains an optional policy:

```csharp
public sealed record PowerTargetContext(
    PredictionRouteSegment Segment,
    TimeSpan ElapsedMovingTime,
    PowerEstimate BaselineEstimate);

public interface IPowerTargetPolicy
{
    PowerEstimate Resolve(PowerTargetContext context);
}
```

For every segment, `RoutePredictor` obtains `BaselineEstimate` from a `PowerLookup` built from the
captured rider model. With no policy it uses that estimate unchanged. With a policy it validates and
uses the returned estimate. Policies are deterministic and side-effect free. Sequence, distance,
and tactical-phase decisions are precomputed and read by sequence during simulation.

This interface replaces the earlier `ScaledPowerLookup` proposal. Gradient, elapsed time, and
sequence alone cannot implement distance windows or phase plans; the full segment context can.

### Full-simulation search

NP/IF and time-target handlers evaluate every candidate through the complete physics simulation.
They do not scale a completed result post hoc. This is required because a changed speed changes
segment duration, elapsed-duration power bands, entry speed for the next segment, and the eventual
objective.

The shared `BoundedPacingSearch` shall:

1. evaluate both bounds and a fixed coarse grid;
2. retain every valid candidate and objective value;
3. identify an adjacent sign-changing bracket;
4. bisect within that bracket until the strategy tolerance or evaluation cap is reached; and
5. return the closest valid candidate if no bracket exists or convergence is not reached.

The coarse grid avoids relying on perfect global monotonicity across duration-band transitions.
Candidate simulation failures are retained as diagnostics but do not abort the search while another
valid candidate remains. Cancellation is checked between simulations. Each strategy defines fixed
bounds, tolerance, and a maximum evaluation count so a request cannot create unbounded work.

## Shared Domain and Service Interfaces

```csharp
public enum PacingStrategyType
{
    SegmentSpecificGains,
    NpIfTarget,
    TimeTarget,
    RpeZoneShift,
    VariableMatchBurning
}

public abstract record PacingStrategyDefinition(PacingStrategyType Type);
public abstract record PacingStrategyReport(PacingStrategyType Type);

public sealed record PacingStrategyContext(
    Guid BaselinePredictionId,
    PredictionRoute Route,
    PredictionResult Baseline,
    RiderProfile Profile,
    RiderModel Model);

public sealed record PacingStrategyComputation(
    PredictionResult Adjusted,
    PacingStrategyReport Report,
    IReadOnlyDictionary<int, PredictionAdjustmentAnnotation> Annotations,
    IReadOnlyList<string> Warnings,
    string AlgorithmVersion);

public interface IPacingStrategyHandler
{
    PacingStrategyType Type { get; }
    PacingStrategyComputation Run(
        PacingStrategyContext context,
        PacingStrategyDefinition strategy,
        CancellationToken cancellationToken);
}
```

The dispatcher requires exactly one registered handler for every enabled type and fails startup on
duplicates. A handler receives the already validated subtype matching its `Type`.

`AdjustmentWarningCodes` is a closed catalog separate from `PredictionWarningCodes`. Adjustment
publication rejects unknown warning strings without changing baseline warning validation.

## Persistence

### `prediction_adjustments`

| Column | Type | Meaning |
| --- | --- | --- |
| `Id` | uuid PK | Stable adjustment identifier |
| `PredictionId` | uuid FK | Immutable baseline owner |
| `StrategyType` | varchar(50) | Stable discriminator |
| `StrategyJson` | jsonb | Canonical validated definition |
| `StrategyAlgorithmVersion` | varchar(128) | Algorithm used for reproducibility |
| `State` | varchar(32) | Queued, Running, Succeeded, Failed, Cancelled |
| `MovingSeconds` | double nullable | Adjusted moving time |
| `AverageSpeedMetresPerSecond` | double nullable | Adjusted route average |
| `AveragePowerWatts` | double nullable | Duration-weighted adjusted average |
| `Confidence` | varchar(32) nullable | Adjusted route confidence |
| `Warnings` | jsonb | Known adjustment warning codes |
| `ResultJson` | jsonb nullable | Canonical typed strategy report |
| `CreatedAt` | timestamptz | Creation instant |
| `CompletedAt` | timestamptz nullable | Terminal instant |

Index `(PredictionId, CreatedAt DESC)` supports the detail page. The baseline foreign key cascades.
The table contains common query/display fields; strategy-specific fields remain in typed result JSON
instead of creating sparse columns for every strategy.

### `prediction_adjustment_segments`

The composite primary key is `(AdjustmentId, Sequence)`. Rows contain adjusted power, speed,
segment/cumulative moving seconds, confidence, and nullable `ZoneNumber`, `StrategyPhase`, and
`WPrimeBalanceJoules`. Geometry is joined from the immutable baseline segment with the same
sequence, avoiding a second copy.

Publication validates that adjusted and baseline sequence sets are identical. It clears and inserts
only the publishing adjustment's child rows inside the owner-guarded transaction.

### Job ownership

Add `JobType.AdjustPrediction`. Its `SubjectId` is the adjustment ID. The existing unique queued and
running indexes therefore permit multiple sibling adjustments while preventing duplicate active
jobs for one child.

`TryPublishAsync` locks and verifies the running job, subject, worker ID, and adjustment before
writing. A cancelled, deleted, retried, or lease-expired worker cannot publish stale output.

## HTTP Contracts

### Endpoints

| Method and path | Behavior |
| --- | --- |
| `GET /api/pacing-strategies` | Enabled discriminators and client limits |
| `POST /api/predictions/{id}/adjustments` | Validate and enqueue one adjustment |
| `GET /api/predictions/{id}/adjustments` | Newest-first child summaries |
| `GET /api/predictions/{id}/adjustments/{adjustmentId}` | Typed report and adjusted segments |
| `DELETE /api/predictions/{id}/adjustments/{adjustmentId}` | Cancel active job and delete child |

Creation returns `PredictionAdjustmentSubmissionResponse(AdjustmentId, JobId, PredictionId)`. The
list and detail contracts do not extend `PredictionSummaryResponse` or
`PredictionDetailResponse`, preserving existing clients.

### Strategy request union

The JSON root discriminator is `type` with these exact values:

- `segment-specific-gains`
- `np-if-target`
- `time-target`
- `rpe-zone-shift`
- `variable-match-burning`

The request union contains no `includeBaseline`: the baseline is always retained and retrieved
through its existing endpoint.

The API maps each request subtype to its domain definition before calling services. Canonical JSON
is produced from the domain definition, never stored directly from untrusted request text.

### Adjustment response

Every summary includes ID, baseline ID, strategy type, state, objective summary fields, adjusted
moving time/speed/power, warnings, algorithm version, and timestamps. Detail adds the original
typed strategy, typed result report, and adjusted segments. Per-segment responses reuse baseline
geometry and expose adjusted metrics plus optional zone/phase/W-prime annotations.

## Validation and Failure Semantics

All numeric values must be finite. Strategy JSON is limited to 64 KiB after canonicalization. Lists
are limited to ten items.

Malformed JSON, unknown discriminators, ambiguous union fields, invalid ranges, and values outside
the exact bounds below return `400` with stable strategy-specific error codes before persistence.
A missing baseline/child returns `404`. A baseline that has not succeeded returns
`409 adjustment-baseline-not-ready`. A disabled strategy returns
`409 pacing-strategy-disabled`.

An unreachable goal is a successful calculation: the closest bounded result is stored with
`Converged = false`, an infeasible or equivalent verdict, and a warning. Infrastructure failures,
corrupt baseline/model snapshots, no valid simulation candidate, or invalid computed structures
fail only the adjustment.

Disabling a strategy blocks new adjustments but leaves stored children readable. Baseline state,
segments, warnings, and completion time remain immutable in every path.

## Strategy 1: Segment-Specific Gains

### Request

`SegmentSpecificGainsStrategy` contains zero to ten ordered rules. Each rule selects exactly one of:

- gradient range with optional inclusive minimum/maximum;
- inclusive sequence range; or
- inclusive cumulative-distance range in metres.

It specifies exactly one operation: factor in `[0.1, 5.0]` or absolute delta in
`[-2000, 2000]` watts. Bounds must be ordered and non-empty.

### Algorithm

For each segment, evaluate rules in request order and apply the first match to the current baseline
estimate:

```text
factor rule: adjusted = baseline watts * factor
delta rule:  adjusted = baseline watts + delta watts
adjusted = max(10 W, adjusted)
```

Unmatched segments retain baseline model power. Clamps add
`segment-gains-power-clamped`. Empty rules produce a valid no-op child with
`segment-gains-no-rules`.

### Report

The report contains each rule's match count, unmatched rule indexes, clamped sequences, adjusted
moving time, and time/speed/power deltas from the immutable baseline.

## Strategy 2: Normalized Power / Intensity Factor

### Request

`NpIfTargetStrategy` contains target IF in `(0, 1.5]`, FTP in `[1, 2000]` watts, and scaling mode
`Proportional` or `Additive`.

### NP calculation

Reconstruct a one-second power series from adjusted segment powers and durations. Starting with the
thirtieth sample, compute a trailing 30-second mean, raise each mean to the fourth power, average
those values, and take the fourth root. This follows the published Normalized Power calculation
steps. TrainingPeaks also cautions that NP is not useful for short intervals; adjustments under ten
minutes therefore carry `np-if-short-route-approximation`. Routes under 30 seconds use
duration-weighted mean power and the same warning. See [TrainingPeaks' calculation description](https://www.trainingpeaks.com/coach-blog/normalized-power-how-coaches-use/).

### Search

The objective is `achieved NP - (FTP * target IF)`.

- proportional mode searches multiplier `[0.1, 5.0]` and returns
  `max(0, baselineEstimate.Watts * multiplier)`;
- additive mode searches delta `[-2000, 2000]` watts and returns
  `max(0, baselineEstimate.Watts + delta)`.

Every objective evaluation uses the full simulation and computes NP from its resulting durations.
Tolerance is 0.5 W with at most 40 total simulations. Non-simulatable zero-power uphill candidates
are invalid candidates, not fatal search errors.

### Report

Store target and achieved NP/IF, FTP, scaling mode, scale/delta, convergence, evaluation count, and
comparison deltas. Values below multiplier 0.5 or above 2.0 retain the draft's low/high-intensity
warnings.

## Strategy 3: Time Target

### Request

`TimeTargetStrategy` contains target moving seconds in `[1, 172800]`, distribution mode
`Proportional` or `ClimbFocused`, optional climb bias, and a feasibility-report flag. Climb-focused
mode requires bias `[1.0, 2.0]`; proportional mode rejects a supplied bias.

`EvenEffort` is not an accepted discriminator. The previous draft never defined a testable
metabolic-cost function, while zone shift supplies a concrete effort-target mechanism.

### Search

The outer scale lies in `[0.3, 4.0]`; the objective is
`simulated moving seconds - target moving seconds`. Tolerance is 30 seconds with at most 40
simulations.

Proportional mode multiplies every model estimate by the outer scale.

Climb-focused mode defines climbs as gradient at least 3%. Let `f` be their fraction of baseline
moving time and `b` the requested bias. For outer scale `S`:

```text
normalizer = f * b + (1 - f)
climb scale = S * b / normalizer
other scale = S / normalizer
```

This preserves one search dimension and a baseline-time-weighted mean factor of `S`. A route with no
qualifying climb falls back to proportional and adds `time-target-no-climbs`.

### Feasibility

For every adjusted segment, compare required watts with the captured model's estimate at that
segment and adjusted elapsed time. Aggregate ratios by gradient band using adjusted moving time.
Classify the worst band as:

- Achievable: ratio at most 1.2;
- Challenging: above 1.2 and at most 1.5;
- Extreme: above 1.5 and at most 2.0; or
- Impossible: above 2.0 or no search bracket.

Store target/achieved time, convergence, scale factors, evaluation count, band summaries, verdict,
and comparison deltas.

## Strategy 4: RPE / Zone Shift

The product name remains recognizable to riders, but the calculation targets power zones and does
not claim to calculate subjective RPE.

### Request

`RpeZoneShiftStrategy` selects `FtpBased` or `ModelInferred`, optional FTP, and one to ten ordered
assignments. FTP-based mode requires FTP `[1, 2000]`; model-inferred mode rejects FTP.

An assignment targets all segments or a gradient range, chooses a legal zone, and selects
`LowerBound`, `Midpoint`, or `UpperBound`. At most one all-segments assignment is allowed.

### Zone resolution

FTP mode supports seven zones anchored to the accepted threshold percentages: 55%, 75%, 90%, 105%,
120%, and 150%. These correspond to the commonly published seven-zone ranges; see [Garmin's zone table](https://www.garmin.com/en-GB/blog/cycling-training-zones-guide/).

Zone 7 is open-ended, so its concrete targets are 151% FTP at the floor, 160% at midpoint, and a
200% cap at the ceiling. Ceiling use adds `rpe-zone-z7-capped`.

Model-inferred mode treats `PowerModel.GlobalTypicalWatts` as the Zone 3 midpoint and derives
`inferredThreshold = GlobalTypicalWatts / 0.83`. It exposes five broad ranges: through 55%, 75%,
90%, 105%, and 150% of inferred threshold. Every result adds `rpe-zone-threshold-inferred`; low
model confidence adds `rpe-zone-model-low-confidence`.

Closed-zone lower/upper placement targets five watts inside the boundary; midpoint averages the
bounds. Gradient assignments are evaluated in request order before the all-segments fallback.
Unmatched segments retain their baseline estimate.

### Report

Aggregate adjusted moving time by resolved zone. Store the threshold and whether it was inferred,
zone boundaries, assignment match counts, average power, NP, time and percent in each zone, and
comparison deltas.

## Strategy 5: Variable Match-Burning

### Request

`VariableMatchBurningStrategy` accepts optional CP `[1, 2000]` watts, optional W-prime
`[1000, 100000]` joules, one to ten burn windows, conservation duration `[0, 300]` seconds and
target `[0.5, 1.0]` CP, recovery duration `[0, 600]` seconds and target `[0.5, 0.9]` CP, a fatigue
report flag, and an optional phase-refinement flag.

Each window selects exactly one gradient, distance, or sequence range and exactly one intensity:
absolute `[10, 2000]` watts, percent CP `[0.5, 3.0]`, or a CP-anchored zone.

### Capacity resolution

Use supplied CP/W-prime when present. Otherwise infer:

```text
CP = 0.95 * TypicalWatts for grade -1:1 and duration 180:+
W-prime = (TypicalWatts for grade 1:3 and duration 0:30 - CP) * 900 seconds
```

Missing CP evidence falls back to `0.95 * GlobalTypicalWatts` with a low-confidence warning.
Missing or non-positive W-prime evidence falls back to 15,000 J with
`match-burning-wprime-inferred-default`. Reports distinguish supplied, inferred, and fallback
values.

### Phase plan

Resolve burn membership from immutable baseline segments. Overlapping windows form one continuous
burn phase; the first matching request window supplies intensity. Walk backward and forward using
baseline segment durations to assign conservation and recovery. Priority is
`burn > recovery > conservation > baseline`.

The optional refinement runs once, recomputes conservation/recovery membership from adjusted
durations, and reruns only if assignments changed. It cannot iterate further.

### W-prime balance

Track constant segment power using exact segment duration. Above CP, expenditure is linear:

```text
balance = balance - (power - CP) * duration
```

Below CP, recover the expended amount exponentially:

```text
DCP = CP - power
tau = 546 * exp(-0.01 * DCP) + 316
expended = W-prime - balance
balance = W-prime - expended * exp(-duration / tau)
```

This replaces the earlier fixed 0.6 linear recovery, which is not the Skiba model named by the
draft. The original study describes linear expenditure and exponential reconstitution whose time
constant depends on recovery power; see [Skiba et al., 2012](https://pubmed.ncbi.nlm.nih.gov/22382171/)
and the [critical-power review with the published equation](https://pmc.ncbi.nlm.nih.gov/articles/PMC5371646/).

Clamp balance to its starting value. Record the first crossing of zero, clamp displayed balance to
zero, and mark that and all later points infeasible while route simulation continues. Classify the
minimum balance as Manageable (at least 30%), Aggressive (10% to below 30%), Risky (above zero to
below 10%), or Infeasible (zero reached).

The UI and API describe this output as an estimate. CP/W-prime and recovery kinetics vary between
riders, especially when inferred rather than measured.

### Report

Store CP, W-prime, provenance flags, phase counts, overlap/unmatched-window warnings, minimum
balance, depleted fraction, critical sequences below 20%, first infeasible sequence, verdict,
refinement status, per-segment phase/balance annotations, and comparison deltas.

## Client Experience

The Predictions page remains baseline-only. A succeeded Prediction Detail page requests adjustment
summaries and displays a `Create adjustment` panel.

The capabilities endpoint controls which picker options render. Each strategy has a focused editor
with its exact fields, defaults, ranges, help text, and client-side validation. Server validation
remains authoritative.

Creation immediately adds a queued card and uses the existing `JobPoller` with the returned job ID.
Cards are newest first and summarize objective and outcome, such as
`Time target 4:30 - achieved 4:31 - Challenging`. Failed and infeasible children remain inspectable.

Selecting a succeeded or infeasible child places its ID in the query string and loads its detail.
The existing baseline summary and geometry remain primary. Power and speed charts add contrasting
adjustment lines; elevation, gradient, and map geometry remain baseline-only. The selected-segment
panel shows baseline, adjustment, and delta metrics. A typed report panel renders the chosen
strategy's report.

Only one child is shown at once. Clearing the query selection restores the baseline-only view.
Deletion requires confirmation and returns to baseline view if the selected child was removed.

Existing baseline GPX and Garmin actions do not gain adjustment selectors in this feature.

## Feature Flags and Configuration

`PacingStrategiesOptions` contains parent `Enabled` and one boolean per stable strategy type. All
flags default false in production configuration. The API and worker both enforce them.

The capabilities endpoint returns only enabled types plus the list-size and numeric limits needed
to render controls. Turning a flag off blocks new creation and execution of not-yet-started work but
does not hide stored results. A running job that already captured an enabled strategy may finish so
flag changes cannot strand data in Running state.

## Testing

### Baseline parity

Run all existing predictor fixtures before and after the route-input refactor and assert identical
public records. The full repository baseline at design time is 1,099 passing tests and zero failures.

### Domain and service tests

- policy context and default-policy parity;
- selector boundaries, precedence, no-op rules, and clamps;
- NP constant/variable fixtures, short-route behavior, and published example calculation;
- coarse-grid/bracket/bisection convergence, evaluation caps, invalid candidates, and no bracket;
- proportional and climb-focused time factors and all feasibility thresholds;
- FTP and inferred zone boundaries, assignments, open Zone 7, and time distribution;
- CP/W-prime inference, phase priority, optional refinement, exponential recovery, depletion, and
  all fatigue verdicts;
- known warning validation and annotation/sequence validation; and
- captured model/profile use rather than current rider state.

### Persistence and workflow tests

- multiple sibling creation and newest-first reads;
- pre-feature baseline segment reconstruction;
- canonical strategy/result JSON round trips;
- adjustment publication transaction and worker ownership;
- retry, cancellation, child deletion, and baseline cascade;
- sibling isolation and immutable baseline assertions;
- unreachable-goal success versus calculation failure; and
- full PostgreSQL migration chain from the initial schema.

### API and client tests

- every polymorphic request/result subtype and malformed discriminator;
- every stable 400/404/409 problem path;
- capabilities and disabled-strategy behavior;
- all four nested adjustment endpoints;
- five bUnit editors, polling, cards, selection/deep links, typed reports, and deletion;
- baseline/adjustment chart dataset construction and synchronized selection; and
- failed, cancelled, disabled, non-converged, and infeasible presentation.

Tests assert maximum simulator invocation counts rather than wall-clock timings.

## Rollout

Delivery uses independently releasable vertical slices:

1. shared adjustment schema, repositories, jobs, route-input parity refactor, policy interface,
   contracts, endpoints, flags, and baseline comparison shell;
2. segment-specific gains end to end;
3. time target end to end;
4. NP/IF target end to end;
5. zone shift end to end; and
6. variable match-burning end to end.

The parent flag stays off until shared migration/parity tests pass. Each strategy flag stays off
until its handler, persistence, API, editor, result report, and back-testing gate pass. Existing
baseline behavior is never behind a pacing flag.

Back-testing gates retain the source requirements where measurable: time target within 5% on the
historical set, NP/IF time recovery within 3%, zone distributions on representative rides, and
match-burning climb speed within 5% plus qualitative fatigue review. These gates validate rollout;
they do not block deterministic unit/integration completion when suitable private ride fixtures are
unavailable in CI.

## Alternatives Rejected

### Child predictions

Representing each adjustment as another `PredictionEntity` would reuse repository code but pollute
history and force baseline/adjustment distinctions into exports, deletion, and every query.

### Generalized scenarios

Moving baseline and children into a common scenario table is conceptually uniform but rewrites the
stable prediction, GPX, Garmin, and visualization paths. The additive child model gives the needed
one-to-many behavior without that risk.

### Post-hoc result scaling

Changing watts or times on completed segments cannot preserve sequential physics or elapsed-duration
power lookup. Full reruns are required.

### Strategy-at-submission

Submitting strategy JSON with a GPX couples baseline creation to one secondary choice and cannot
naturally support revisiting one immutable baseline with many alternatives. Adjustment creation is
therefore a separate post-baseline resource.

## Narrative Consequence

This specification is a correction to the accepted narrative entry
`docs-add-pacing-strategy-implementation-plans`. That entry remains unchanged as evidence of the
earlier framing. A new correction fragment records the append-only adjustment architecture and the
scientific/algorithmic clarifications.

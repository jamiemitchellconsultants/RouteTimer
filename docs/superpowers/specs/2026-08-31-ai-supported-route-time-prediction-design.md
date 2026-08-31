# AI-Supported Route-Time Prediction Design

**Date:** 2026-08-31
**Status:** Approved

## Purpose

RouteTimer shall add a locally trained, rider-specific machine-learning layer that adjusts the
existing typical-power policy before the cycling-physics simulator calculates speed and moving time.
The deterministic rider model remains the permanent baseline and fallback. The learned layer is
published only after it beats that baseline on chronological, whole-ride validation, and it is used
only for routes supported by sufficiently similar training evidence.

The rider shall be able to request either a Typical prediction or a Today prediction. Typical
describes the rider's usual effort for the proposed route. Today additionally considers recent
training load, fatigue, fitness, and inactivity. If recent history is not known to be current, Today
falls back to Typical. If the route itself is unsupported, either mode falls back to the deterministic
prediction.

The Training surface shall show restrained, transparent progress toward AI evaluation. It reports ride
count and route variety, identifies the most useful evidence gap, and distinguishes having enough data
from having proved that AI is better.

All model training and inference remain on the RouteTimer deployment. No ride, route, feature, or model
data is sent to an external AI service.

## Prerequisite

The approved weather-aware training work must be deployed and its historical backfill complete before
AI training is enabled. Only weather-ready training rides may contribute to learned effort labels.
Historical validation replays each held-out ride in its recorded weather so headwinds, air density,
precipitation, and wet descents are not mislearned as rider behaviour.

This is a hard sequencing dependency, not a runtime dependency for ordinary deterministic predictions.
RouteTimer continues to provide calm, dry deterministic predictions while weather enrichment or AI
training is unavailable.

## Confirmed Product Decisions

- AI support activates automatically only after a challenger beats the deterministic baseline and the
  proposed route passes its evidence gate.
- The deterministic result is always calculated first and retained as the comparison and fallback.
- Typical is the default prediction mode so existing product semantics do not change silently.
- Today is explicitly selected and adds a separately validated recent-training-state adjustment.
- Today falls back to AI-supported Typical when recent history is incomplete or stale.
- If Typical AI is unavailable or the route gate rejects the route, prediction falls back to the
  deterministic result.
- Global AI readiness and route-specific support are separate. Global readiness describes the breadth
  of the training history; route support decides whether AI may adjust a particular prediction.
- Readiness uses percentages, progress bars, and restrained status text. It has no points, badges,
  streaks, leaderboards, or celebratory effects.
- Reaching a ride-count or readiness threshold permits evaluation but never publishes a model by
  itself.
- The learned layer predicts a rider-effort multiplier. The existing physics simulator remains
  responsible for final power, speed, segment time, and total moving time.
- Initial candidates are regularised additive models suitable for small tabular datasets. Neural
  networks and an end-to-end route-time replacement are outside the initial scope.
- Published model versions and completed predictions are immutable.

## Scope

The feature includes:

1. historical whole-ride feature extraction and weather-aware deterministic replay;
2. a bounded solver that derives the effective rider-power multiplier for a held-out ride;
3. Typical and Today additive model components;
4. chronological challenger training, route-support calibration, and publication gates;
5. global readiness, evidence-strength, and history-freshness calculations;
6. immutable AI model and derived-example persistence;
7. Typical and Today prediction modes with explicit effective-mode and fallback metadata;
8. Training, prediction-form, prediction-result, and shared model-status UI changes;
9. shadow, comparison, and automatic-serving rollout stages; and
10. local-only training, operational metrics, and deterministic automated tests.

The feature does not include:

- replacing cycling physics with a direct route-time model;
- a neural network, large language model, hosted inference API, or shared cross-rider model;
- predicting race-best performance or an explicit easy/race intent;
- using target-ride power, heart rate, cadence, or completion time as input features for that ride;
- claiming a clinical or proprietary fitness score such as TSS;
- changing historical predictions when a model is rebuilt;
- overriding explicit pacing adjustments; or
- enabling AI training before weather-aware historical replay is available.

## Existing Boundaries Reused

`PowerModelBuilder` currently creates a robust gradient-by-elapsed-duration typical-power grid.
`PhysicsCalibrator` and `DescentLimitBuilder` learn rider-specific physical and descending behaviour.
`RoutePredictor` looks up baseline watts, optionally passes them through `IPowerTargetPolicy`, and then
integrates the route segment by segment. `ModelValidator` already holds out whole activities and
compares predicted with recorded moving time.

The learned layer uses these boundaries rather than duplicating them:

- historical replay invokes the same route processor, deterministic builders, environment-aware
  predictor, and physics implementation used by production;
- AI effort is applied through `IPowerTargetPolicy`;
- the prediction rerun produces ordinary `PredictionSegment` values; and
- the immutable rider-model and prediction snapshot conventions extend to AI artifacts.

The current leave-one-ride-out validator remains useful for the deterministic model. AI validation is
separate and chronological because Today features and rider capability change over time; later rides
must never help predict an earlier one.

## Chosen Architecture

### Model relationship

Each `RiderAiModel` records the exact deterministic `RiderModel` version current when the AI model was
trained. Serving compatibility is based on the deterministic algorithm version, feature schema, and
unchanged rider profile rather than exact model ID. This lets the last validated AI model remain active
while a newly uploaded ride triggers both rebuilds, but suspends it after a profile or deterministic
algorithm change until a compatible successor passes validation. Its learned log multiplier is
additive:

```text
log(finalMultiplier) = typicalRouteComponent + optionalTodayStateComponent
finalWatts = deterministicTypicalWatts * exp(log(finalMultiplier))
```

The Typical artifact contains the route component. The optional Today artifact is trained on the
remaining log-multiplier residual and contains a centred training-state component whose neutral,
long-term state is zero. This makes Typical independent of current freshness and permits Today to be
withheld or withdrawn without changing Typical.

The resulting multiplier is supplied uniformly to the power-target policy, after which the physics
simulator reruns from the route start. Uniform effort adjustment is deliberately less expressive than
a segment-level ML policy, but it preserves coherent power, speed, segment timing, and timed-GPX output
with the available number of independent rides.

Explicit user-selected pacing policies are outside this feature's serving path. AI support applies to
the baseline Typical or Today prediction, not to an adjusted pacing strategy.

### Components

`HistoricalBaselineReplayer` orders eligible activities by start time. For every target ride after the
first, it attempts to rebuild the deterministic model from earlier rides only, reconstructs the target
route, resolves the target's historical environment, and produces a baseline prediction. A ride gets
no derived example when its earlier prefix cannot build a valid deterministic model.

`HistoricalRideFeatureExtractor` converts the route and baseline result into a versioned feature row.
It accepts only values knowable before the target ride began.

`TrainingStateCalculator` calculates time-decayed training-state features from rides strictly earlier
than a supplied instant. It also assesses whether the deployment has enough preceding history and
whether that history is confirmed current.

`EffortMultiplierSolver` performs a bounded monotonic search for the uniform multiplier whose
weather-aware simulated moving time matches the target ride. The initial search range is `0.50` to
`1.50`. A ride is excluded with a stable reason if no finite solution exists, the solution reaches a
bound, or the replay itself is invalid.

`AiEffortModelTrainer` trains the fixed candidate set, performs nested chronological evaluation,
calibrates route support, and returns a publishable artifact or a rejected-challenger result.

`AiReadinessService` calculates the global evidence score independently of whether a challenger has
won. `AiPredictionPolicy` validates a published artifact and its compatibility with the current
deterministic algorithm, feature schema, and rider profile; evaluates the route gate; chooses the
effective mode; evaluates the bounded multiplier; and invokes the existing predictor.

`BuildAiModelJobHandler` owns asynchronous orchestration. It is a separate job from `BuildModel`, so an
AI failure cannot make the deterministic model unavailable.

## Training Examples and Features

There is exactly one independent AI training example per qualifying activity. Segment samples may be
aggregated into features but are never treated as independent model rows.

### Typical route features

The initial schema contains:

- processed route distance, ascent, descent, and baseline moving time;
- ascent metres per kilometre;
- fractions of distance below -6%, -6% to -3%, -3% to 3%, 3% to 6%, and above 6% gradient;
- descent-curvature median and 90th percentile;
- deterministic average power and weighted power by gradient region;
- deterministic low-confidence and extrapolated-time shares; and
- deterministic calibration and learned-descent availability flags.

Position, raw coordinates, activity name, upload source, equipment identifiers, and absolute calendar
date are not features. Historical weather is supplied to replay but is not a Typical effort feature;
its purpose is to remove environmental effects from the label.

### Today training-state features

For each target instant, the state calculator produces:

- exponentially decayed moving hours over 7-day and 42-day time constants;
- exponentially decayed measured mechanical work over the same time constants;
- ride counts and active-day counts over the preceding 7 and 42 calendar days;
- days since the most recent eligible ride; and
- recent ride intensity relative to the earlier deterministic typical-power model.

The values are centred on the rider's long-term medians before fitting the Today component. Recorded
power is used only for earlier rides contributing to state; target-ride power is never an input.
RouteTimer exposes these as transparent activity-history summaries, not as physiological truth.

Today evaluation requires at least 42 preceding calendar days of retained activity history. The
current history must be confirmed through a time no more than 48 hours before prediction. A successful
Garmin activity-history check advances this marker even if it discovers no new rides. Manual-upload
riders may explicitly confirm that their uploaded history is current. A long genuine rest therefore
remains representable; an old last-ride timestamp alone does not imply stale data.

## Initial Candidate Models

The fixed initial candidate set is:

1. an elastic-net linear regressor;
2. a generalised additive regressor with bounded, versioned smooth terms; and
3. the deterministic no-adjustment baseline.

Fitting gives each ride equal top-level weight and uses robust residual weighting so an unusually easy
or hard self-paced ride cannot dominate the result. Hyperparameter ranges and model-selection rules are
algorithm-versioned and bounded. The stored artifact contains application-owned intercepts,
coefficients, normalisation values, and additive term knots rather than an opaque executable library
blob.

LightGBM may be introduced as a later algorithm version after materially more independent evidence is
available and it passes the same validation boundary. Adding it is not part of this design's initial
implementation.

## AI Readiness

Readiness describes evidence available for evaluation and has a maximum of 100%:

- Ride count contributes 50 points, linearly from zero to 60 eligible, weather-ready rides.
- Duration variety contributes 25 points across `<1 hour`, `1-2 hours`, `2-4 hours`, and `4+ hours`.
- Terrain variety contributes 25 points across flat (`<7 m ascent/km`), rolling (`7-15 m/km`), and
  climbing-intensive (`>15 m/km`) routes.

A duration or terrain bucket is supported by at least three qualifying rides. Partial bucket coverage
earns its proportional share of that contributor: each bucket's fraction is
`min(qualifyingRideCount / 3, 1)`. Repeated rides increase count and consistency but do not create false
variety.

The first challenger may run with at least 30 qualifying rides, two supported duration buckets, and two
supported terrain buckets. Full readiness is neither required for training nor sufficient for
publication.

The UI displays the percentage, the three contributors, the strongest supported region, and one next
suggestion selected from the largest evidence gap. Examples include “Add a ride longer than four
hours” and “Your evidence is strongest for rolling rides between one and three hours.” Suggestions are
descriptive, not prescriptive training advice.

Readiness states are:

- `CollectingEvidence` before the challenger threshold;
- `ReadyToEvaluate` when the threshold is met but no completed challenger exists;
- `Evaluating` while a build is running;
- `AiSupported` when a published Typical artifact exists;
- `BaselineStillBest` when the latest challenger was valid but did not win; and
- `Reevaluating` when a prior published artifact remains active during a new build.

## Chronological Validation and Publication

Activities are ordered by activity start time with stable ID ordering for equal timestamps. Derived
examples begin with the second ride when its earlier prefix can build a valid deterministic baseline.
The first 15 qualifying rides form the AI seed and are expected to provide up to 14 derived examples.
Each subsequent ride is an outer expanding-window fold: candidate selection, route-gate calibration,
and fitting see only the prefix before that ride; the gate then decides whether the next ride is
supported without seeing its outcome; and the chosen candidate and contemporaneous deterministic
baseline are scored only when it is supported.

Within each outer prefix, candidate, hyperparameter, and route-gate selection use an inner
expanding-window validation over the derived examples in that prefix, with at least eight earlier
examples required to fit an inner candidate. This nested boundary prevents the same target outcome
from choosing the candidate or support boundary that reports its performance. With 30 qualifying
rides, the procedure can attempt 15 outer validation rides; publication may require more rides if
fewer than 15 pass their independently calibrated route gate.

Typical and Today are scored separately against the same applicable deterministic folds. Today scores
only folds with 42 preceding days and valid current-history semantics. It is published only as an
optional component of a publishable Typical model.

For each mode, RouteTimer records whole-ride:

- absolute error in seconds and minutes;
- absolute percentage error;
- signed percentage error;
- median absolute percentage error; and
- 90th-percentile absolute percentage error.

Typical publication requires all of the following on at least 15 supported outer folds:

- median absolute percentage error improves by at least 10% relative to the deterministic median;
- the absolute median improvement is at least one percentage point;
- AI P90 absolute percentage error is no worse than the deterministic P90;
- AI median signed percentage error is within `-3%` to `+3%`;
- every selected prediction and artifact value is finite and valid; and
- the route-support calibration described below succeeds.

Today uses the same improvement, tail-error, bias, and validity gates on at least 15 supported,
Today-applicable outer folds. Historical Today folds require 42 preceding days of retained history but
do not invent retrospective freshness confirmations; the 48-hour confirmation rule applies when
serving a present-day request. If Today fails, Typical may still publish and Today requests fall back
to Typical.

A rejected challenger and its safe metrics remain available for diagnosis but are never served. A
successful build publishes a new immutable version atomically. The previous published model remains
active until that transaction completes.

## Route-Specific Support Gate

Global readiness does not authorise every route. The gate uses a robustly normalised feature space
containing baseline duration, distance, ascent density, grade-region fractions, and descent curvature.
Normalisation medians and interquartile ranges are captured in the AI model.

Within each outer prefix, the trainer records inner-validation distance to the five nearest earlier
rides and whether AI beat the deterministic baseline. It selects the largest neighbour-distance
boundary for which aggregate inner validation still satisfies the publication gates, then applies that
boundary to the unseen outer ride. After outer validation passes, the final serving boundary is fitted
from all available derived examples using the same versioned method; it does not rewrite the reported
outer results. A prediction is supported only when:

- at least five training rides exist within that calibrated boundary;
- baseline duration, ascent density, steep-gradient share, and descent curvature remain within the
  inclusive supported ranges observed in qualifying training examples; and
- all route features are finite and compatible with the captured schema.

The route-match percentage is a monotonic presentation of neighbour distance between the closest
observed match and the calibrated rejection boundary. It is an evidence-similarity indicator, not a
probability of correctness.

Among supported outer folds nearest to the request, the median and P90 absolute percentage errors form
the displayed comparable-error range. The UI says “Comparable validation error: 6-11%” or equivalent,
not “90% confidence.”

Today has an additional state-support gate. Every centred 7-day and 42-day load, activity-frequency,
intensity, and inactivity feature must remain within the inclusive ranges observed on supported Today
outer folds. An out-of-range state falls back to Typical with `today-state-unsupported`; it is not
clamped into a state the model has seen.

## Prediction Behaviour

Prediction submission accepts `mode=typical|today`; omission means `typical`. There is no separate AI
toggle.

For every request, RouteTimer:

1. calculates the deterministic prediction;
2. loads and validates the published AI artifact compatible with the current deterministic algorithm,
   feature schema, and rider profile;
3. evaluates the route-support gate;
4. evaluates the Typical route component when supported;
5. for Today, verifies freshness and the state-support gate, then adds the Today state component;
6. clamps the final multiplier to the intersection of `0.75-1.25` and the minimum-to-maximum multiplier
   range observed in successful outer validation;
7. applies the multiplier through `IPowerTargetPolicy` and reruns the physics simulator; and
8. persists the final segments plus AI provenance and the deterministic comparison.

The effective-mode fallback order is:

```text
requested Today -> AI Today -> AI Typical -> deterministic
requested Typical -> AI Typical -> deterministic
```

Today freshness failure uses `today-history-stale`; unavailable or failed Today publication uses
`today-model-unavailable`; unsupported current state uses `today-state-unsupported`. Route rejection
uses a specific stable reason such as
`ai-route-duration-unsupported`, `ai-route-climbing-unsupported`, or
`ai-route-neighbour-support-insufficient`. A runtime AI evaluation failure uses
`ai-evaluation-fallback`.

An empty or invalid multiplier-range intersection makes the artifact unpublishable. AI errors never
replace a valid deterministic result with a failed prediction. They produce a safe warning, record an
operational diagnostic, and return the deterministic result. Existing deterministic calculation
failures retain their existing failure semantics.

## Persistence

### Rider AI models

`rider_ai_models` stores:

- immutable ID, creation time, algorithm version, and feature-schema version;
- referenced deterministic rider-model ID;
- deterministic algorithm compatibility version and rider-profile snapshot;
- readiness snapshot and qualifying-ride count;
- Typical artifact and optional Today artifact in validated structured JSON;
- feature normalisation and supported ranges;
- route-gate calibration and comparable-error buckets;
- Today state-feature supported ranges;
- deterministic, Typical, and Today validation metrics;
- training-period bounds;
- publication state and stable rejection reason; and
- superseded published-model reference when applicable.

Only one valid version is current. Predictions keep a direct reference to the version they used.

### Derived examples

`ai_training_examples` stores activity ID, feature-schema and replay-algorithm versions, a digest of the
chronological evidence prefix, feature JSON, solved log multiplier, training-state JSON, derivation
time, and nullable exclusion reason. The unique key is activity, both versions, and the prefix digest.
An inserted, deleted, reparsed, or re-enriched earlier ride therefore invalidates every affected later
example instead of reusing a row derived from a different history. These rows are rebuildable caches;
retained activities and weather observations remain authoritative.

Deleting or reparsing an activity invalidates its derived examples and queues deterministic and AI
rebuilds. Accepted historical predictions are unchanged.

### Prediction additions

Predictions store requested mode, effective mode, deterministic baseline moving seconds, nullable AI
model ID, applied multiplier, route-match percentage, neighbour count, comparable median/P90 error,
and nullable fallback reason. Final prediction segments remain the authoritative served result.

### History freshness

A single-rider history state stores `confirmed_through` and its source (`GarminCheck` or
`ManualConfirmation`). Successful Garmin history retrieval advances the time even when it imports no
activities. A manual authenticated operation may advance it to the current server time; it cannot set
an arbitrary future time.

## Jobs and Data Flow

The weather-aware flow is extended as follows:

```text
training activity saved
  -> historical weather enrichment
  -> coalesced BuildModel job
       -> publish deterministic rider model
       -> recalculate readiness
       -> coalesce BuildAiModel job when minimum evidence exists
            -> derive/reuse chronological examples
            -> train and validate challengers
            -> calibrate route support
            -> atomically publish winner, or record rejection
```

Only one AI build runs at a time. New evidence arriving during a build coalesces into a successor build.
Cancellation before publication leaves the current published artifact unchanged. AI progress stages
include deriving examples, training Typical, validating Typical, training Today, validating Today,
calibrating support, and publishing.

## API and Contracts

`GET /api/models/current` retains deterministic readiness and adds a nested AI status containing:

- readiness percentage and state;
- ride-count, duration-variety, and terrain-variety contributors;
- strongest evidence text and next-evidence suggestion;
- challenger/build state and safe rejection reason;
- published algorithm/model identifiers and validation comparisons;
- Today availability; and
- history confirmation time and source.

Prediction creation accepts the requested mode. Prediction summary and detail contracts add effective
mode, deterministic baseline time, AI effort-adjustment percentage, AI model version, route match,
supporting ride count, comparable-error range, and fallback reason.

An authenticated `POST /api/training-history/confirm-current` operation advances manual history
freshness to the server's current time. It carries no user-supplied timestamp.

Contract additions are optional where necessary to read predictions created before this feature.
Unknown persisted enum values or incompatible artifacts are rejected at the repository boundary rather
than guessed.

## User Experience

### Training and shared model status

The Training page shows “AI readiness” with one overall percentage and three labelled progress bars.
It shows evidence strengths, one next suggestion, model-evaluation state, and Today freshness. Manual
upload users receive a “My uploaded history is current” action with an explanation that Today assumes
all recent rides have been supplied.

The shared model-status component presents a compact version on Home and Predictions. Deterministic
model readiness remains distinct; a rider may have a ready deterministic model while still collecting
AI evidence.

Example states include:

```text
AI readiness: 68%
38 of 60 rides | 3 of 4 durations | 2 of 3 terrain types
Best next addition: a ride longer than four hours.
```

```text
Baseline still best
There is enough varied history to evaluate AI, but it has not yet improved on your existing model.
```

### Prediction form and result

The form offers Typical and Today as ordinary mode choices and explains Today's dependence on current
history. Typical is initially selected.

An AI-supported result includes final and deterministic times, multiplier, route match, supporting
ride count, comparable validation error, effective mode, and model version. It uses observational
phrasing for additive contributions, such as “Long duration reduced expected effort,” and never claims
causation or physiological certainty.

Fallback examples include:

```text
Today adjustment unavailable because recent activity history is not current.
AI-supported Typical prediction used instead.
```

```text
Deterministic prediction used. AI support is available generally, but this route is longer and steeper
than your supported history.
```

## Failure Semantics

- AI job failure leaves deterministic readiness and the current published AI artifact intact.
- A corrupt or incompatible current artifact is quarantined and not served.
- Non-finite features, coefficients, terms, multipliers, or outputs cause deterministic fallback.
- An unsolved historical target excludes that example with a stable reason; it does not abort the
  entire build unless fewer than the required examples remain.
- Insufficient route support is expected product state, not an error response.
- Stale Today history is expected fallback state, not an error response.
- Prediction persistence is atomic: it never stores a final prediction with partial AI metadata.
- Safe diagnostics exclude coordinates, raw samples, feature vectors, activity names, and model JSON.

## Privacy and Operations

AI training and inference perform no network calls. The separate weather prerequisite retains its own
documented provider disclosure, but the learned-model subsystem sends nothing externally.

Operational metrics include build duration, examples derived/reused/excluded, qualifying ride count,
outer validation fold count, deterministic and AI metrics, publication/rejection outcome, multiplier
distribution, route-gate rejection reasons, Today fallback frequency, and AI runtime fallback count.
They contain no coordinates or per-ride feature values.

Configuration independently controls AI building and AI serving. Algorithm-affecting thresholds are
compiled into and recorded with the algorithm version; configuration may disable behaviour but may not
silently change model semantics.

## Testing

### Feature and leakage tests

- Typical features contain only the approved route and deterministic fields.
- Today features use activities strictly earlier than the target instant.
- Target-ride power, heart rate, cadence, completion time, and later activities cannot enter features.
- Equal timestamps use stable ordering and never cross the chronological boundary.
- Weather-aware replay prevents synthetic headwind and wet-descent differences from changing solved
  rider effort when the underlying rider effort is unchanged.

### Model and numerical tests

- The multiplier solver recovers known synthetic multipliers and rejects absent, boundary, and
  non-monotonic solutions.
- Log-space fitting and exponentiation remain finite and positive.
- Elastic and additive artifact round-trips reproduce scores exactly within defined numeric tolerance.
- Synthetic route and training-state effects are recovered without learning deliberately irrelevant
  fields.
- Inference bounds intersect global and observed validation ranges correctly.

### Validation and gate tests

- Every outer fold and its inner model and route-gate selection use only earlier rides.
- Baseline and challenger metrics use identical scored rides.
- Each publication threshold has below, exact-boundary, and above tests.
- Typical may publish while Today is withheld, never the reverse.
- Five-neighbour support, calibrated distance, critical ranges, Today state ranges, and route-match
  presentation are deterministic.
- Unsupported long, steep, or unusually technical routes fall back with the specific reason.

### Workflow and persistence tests

- A weather-aware deterministic build queues one coalesced AI build when readiness permits.
- A failed/cancelled/rejected build leaves the current published model unchanged.
- Activity deletion invalidates derived rows and queues both rebuilds without changing old predictions.
- Repository loading rejects unknown versions, malformed JSON, non-finite values, and invalid bounds.
- Prediction writes atomically capture requested/effective modes, comparison, provenance, and segments.
- Garmin checks and manual confirmation update freshness with the correct source and never accept future
  user timestamps.

### API and client tests

- Existing pre-AI predictions deserialize with absent optional AI fields.
- Typical is the default submission mode.
- Today follows `AI Today -> AI Typical -> deterministic` fallback order.
- Readiness contributors, strongest evidence, and next suggestion render independently.
- Model build/rejection status does not obscure deterministic readiness.
- Result language distinguishes route match from prediction confidence and correlation from causation.
- AI disabled, unavailable, unsupported, invalid, or failed preserves existing deterministic outputs.

## Rollout and Rollback

Rollout has three explicit serving stages:

1. **Shadow training:** readiness and challengers are built and inspected, but no prediction evaluates
   an AI artifact.
2. **Comparison:** production requests evaluate eligible AI results for operational comparison while
   returning and persisting deterministic results only.
3. **Automatic support:** published artifacts affect supported predictions under the approved gates.

Advancement requires successful weather backfill, representative fixture and synthetic tests, stable
job operation, reproducible artifacts, and observed fallback behaviour. It is an operator decision,
not an automatic elapsed-time transition.

Rollback disables AI serving immediately, returning all new requests to deterministic behaviour. AI
building can be disabled separately. Additive schema and existing artifacts remain for diagnosis;
rollback does not delete models, examples, or prediction provenance.

## Acceptance Criteria

1. Weather-aware training replay is deployed before AI example derivation can run.
2. Global readiness separately reports ride count, duration variety, and terrain variety.
3. Reaching a readiness threshold starts evaluation but cannot publish a model without a validation
   win.
4. Historical examples and nested validation contain no future or target-ride leakage.
5. Published Typical AI beats the contemporaneous deterministic baseline under every agreed gate.
6. Today is validated and published independently and falls back to Typical when history is stale.
7. Route-specific support requires five nearby rides, calibrated similarity, and supported critical
   ranges.
8. Unsupported routes use the deterministic predictor with a specific explanation.
9. AI effort is applied through the power-policy boundary and final output comes from a full physics
   rerun.
10. Every AI-supported prediction captures its deterministic comparison, model version, effective
    mode, multiplier, route support, validation range, and fallback state.
11. AI build, artifact, or evaluation failure never prevents an otherwise valid deterministic
    prediction.
12. Training and inference remain local and operational telemetry contains no route or ride evidence.
13. Shadow, comparison, automatic-serving, disable, and rollback controls are independently testable.

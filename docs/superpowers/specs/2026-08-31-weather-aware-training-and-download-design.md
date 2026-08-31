# Weather-Aware Training Interpretation and Timed Download Design

**Date:** 2026-08-31
**Status:** Approved

## Purpose

RouteTimer shall rebuild its rider model after interpreting every usable training ride in the
historical weather that affected it. All future training uploads shall pass through the same weather
enrichment before they can influence a model. Wind, temperature, and precipitation shall correct the
interpretation of the ride without rewriting recorded power or the original FIT-derived samples.

Ordinary predictions remain calm, dry predictions. When downloading a timed GPX, a rider may opt into
a transient recomputation using a route-time forecast that starts at the download time. That operation
changes only the downloaded timestamps; it does not mutate the rider model, the stored prediction, or
any pacing adjustment.

Historical weather comes from Open-Meteo's Historical Weather API at `/v1/archive`. Current and future
conditions for an immediate ride come from Open-Meteo's Forecast API at `/v1/forecast`. The relevant
provider contracts are documented at:

- <https://open-meteo.com/en/docs/historical-weather-api>
- <https://open-meteo.com/en/docs>

## Confirmed Product Decisions

- Weather is attached to immutable training evidence and interpreted at model-build time.
- Recorded training power is never scaled for temperature, wind, or precipitation.
- Historical wind corrects aerodynamic calibration using rider bearing and apparent air velocity.
- Historical temperature and surface pressure determine interval air density.
- Wet intervals do not contribute to CdA/Crr calibration.
- Wet intervals do not contribute to the learned dry descent limit.
- Strong-crosswind descent intervals are excluded when they cannot be normalized reliably.
- Weather-aware leave-one-ride-out validation replays the held-out ride in its historical weather.
- An activity waiting for weather remains stored but cannot influence a model.
- The existing rider model remains usable while weather backfill or a new enrichment is pending.
- Ordinary prediction creation and stored prediction assumptions remain road, calm, dry, and
  moving-only.
- Weather-adjusted export is available only for a timed GPX and only when explicitly selected.
- Download-time adjustment uses a route-time forecast beginning at the server's request time, not a
  single weather snapshot applied to the whole route.
- The adjusted result is built in memory and never persisted.
- Forecast rain does not change target power or Crr. At or above 0.1 mm in the applicable hour, it
  reduces the learned descent cap by a configurable 15%, lowers segment confidence, and adds a
  wet-conditions warning.
- Untimed GPX, baseline timed GPX, Garmin export, saved predictions, and saved pacing adjustments are
  unchanged.
- A legacy prediction captured from a pre-weather model cannot request weather adjustment. It must be
  recreated after backfill so historical wind is not corrected twice.

## Scope

The feature includes:

1. Open-Meteo archive and forecast clients behind an application-owned interface;
2. historical weather persistence and enrichment state per training activity;
3. automatic backfill of existing retained training activities;
4. weather enrichment for every future locatable training upload;
5. weather-aware physics calibration, descent interpretation, and model validation;
6. an environment-aware extension to the route simulator whose default remains calm and dry;
7. an opt-in route-time-forecast timed GPX download;
8. weather state and summaries on the Training and model-status surfaces;
9. deterministic tests with fake provider responses; and
10. deployment, privacy, and operational documentation.

The feature does not include:

- changing measured watts, cadence, heart rate, timestamps, positions, or other FIT evidence;
- estimating physiological heat or cold adaptation;
- applying weather to ordinary prediction submission or stored prediction results;
- saving forecast-adjusted results or forecasts;
- weather-adjusted Garmin course export;
- weather-adjusted pacing adjustments;
- inferring tyre type, water depth, road contamination, snow, or ice;
- a generic wet-road Crr multiplier; or
- live Open-Meteo calls in automated tests.

## Existing Architecture

The current training flow saves a cleaned activity and immediately coalesces a model build:

```text
stored FIT upload
  -> ParseTraining job
  -> FitActivityParser
  -> TrainingCleaner
  -> TrainingActivityRepository.SaveAsync
  -> coalesced BuildModel job
```

`BuildModelJobHandler` loads all activities, enriches their route geometry, builds the typical-power
grid, calibrates physical coefficients, learns descent limits, runs leave-one-ride-out validation, and
saves a new rider-model snapshot. `PhysicsCalibrator` currently treats ground speed as air speed and
uses a fixed air density. `DescentLimitBuilder` treats observed ground speed as a calm, dry rider
preference. `ModelValidator` replays held-out rides in the same calm, dry environment. Wind,
temperature, and precipitation can therefore leak into CdA/Crr, descent limits, and validation error.

The current prediction flow deliberately persists `RoadCalmDryMovingOnly` assumptions. A prediction
captures its rider profile and rider-model ID, and stores immutable output segments. Timed GPX export
adds cumulative segment time to a stored start instant; it does not rerun the simulator.

## Chosen Architecture

### Why observations remain separate from ride samples

Historical weather is interpretation, not recorded ride evidence. `ActivitySampleEntity` remains the
faithful cleaned representation of the FIT file. New weather rows are owned by an activity but are not
copied onto each sample. Model-building services receive a weather resolver that projects persisted
observations onto samples as needed.

This boundary preserves auditability, permits idempotent re-enrichment when the provider or algorithm
changes, and prevents accidental double correction. Rewriting speed or power into calm-weather
equivalents was rejected because later code could no longer distinguish measured values from derived
ones.

### Updated training flow

```text
stored FIT upload
  -> ParseTraining job
  -> parse, clean, and save immutable activity with weather state Pending
  -> EnrichTrainingWeather job(activity id)
       -> select route anchors and UTC interval
       -> Open-Meteo /v1/archive
       -> validate and atomically persist observations
       -> mark activity Ready
       -> coalesce BuildModel job
  -> BuildModel job
       -> load Ready eligible activities only
       -> resolve historical weather per interval
       -> build power, physics, descent, and validation models
       -> save weather-aware rider-model snapshot
```

`ParseTrainingJobHandler` uses the activity ID returned by `SaveAsync` as the weather job subject. It
no longer queues `BuildModel` directly. An ineligible but locatable ride is still enriched so the
Training detail is complete, although it remains excluded from model evidence for its existing quality
reasons. A ride without usable time or location is marked weather-unavailable with a stable reason; if
it would otherwise be eligible, it is excluded from model building.

### Component boundaries

`RouteTimer.Domain` owns validated weather values, wind vectors, environmental conditions, enrichment
state, and weather warning codes. It has no HTTP or Open-Meteo JSON types.

`RouteTimer.Services` owns route-anchor selection, observation interpolation, provider-neutral weather
interfaces, enrichment orchestration, air-density calculation, apparent-wind force calculation,
weather-aware calibration and validation, and transient export recomputation.

`RouteTimer.Persistence` stores activity-owned historical observations and enrichment state, maps them
to provider-neutral service records, and supplies the captured prediction/model material needed for
transient export. It does not call Open-Meteo or implement weather physics.

`RouteTimer.Api` owns Open-Meteo HTTP registration and configuration, contract/error mapping, the
forecast-adjusted GPX query, and startup backfill reconciliation.

`RouteTimer.Client` owns weather status display, the opt-in checkbox, progress state, browser download,
and inline failure presentation.

## Weather Provider Contract

### Provider-neutral interface

The service boundary exposes two deliberately separate operations:

```csharp
public interface IWeatherProvider
{
    Task<IReadOnlyList<WeatherSeries>> GetHistoricalAsync(
        IReadOnlyList<WeatherLocation> locations,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WeatherSeries>> GetForecastAsync(
        IReadOnlyList<WeatherLocation> locations,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}
```

Open-Meteo DTOs stay inside the adapter. Both operations request SI-friendly units and UTC timestamps:

- `temperature_2m` in degrees Celsius;
- `surface_pressure` in hPa;
- `precipitation` in mm for the preceding hour;
- `wind_speed_10m` in m/s; and
- `wind_direction_10m` in degrees.

Historical calls use `/v1/archive`; forecast calls use `/v1/forecast`. The adapter specifies the
configured model explicitly rather than depending on a provider-default change. The initial archive
model is Open-Meteo Best Match, recorded under a stable local provider-version string. Base URLs and
an optional API key remain configurable so a deployment can use an Open-Meteo commercial or self-hosted
endpoint without changing application code.

### Validation

The adapter rejects a response unless:

- every requested location has exactly one series;
- timestamps are strictly increasing UTC instants;
- all requested arrays have matching lengths;
- units match the requested units;
- temperature, pressure, precipitation, wind speed, and wind direction are finite and within broad
  physical bounds;
- precipitation and wind speed are non-negative; and
- the returned range covers the required interval.

Wind direction is normalized into `[0, 360)`. Missing or `null` values make the required interval
incomplete; they are not silently replaced with calm or dry values.

### Route anchors and interpolation

The selector always includes the first and last usable route positions and adds a representative anchor
at each configured 10 km of cumulative route distance. It also starts a new anchor after a geographic
discontinuity. Ten kilometres matches the useful order of magnitude of Open-Meteo's recent global
historical grids without pretending that per-second FIT positions have per-second weather precision.

The provider request spans the UTC hour containing the activity start through the UTC hour containing
the activity end. Each returned observation stores its requested anchor, returned grid coordinate,
valid hour, and values.

At model-build time, an interval resolves weather at its midpoint. Temperature, surface pressure, and
east/north wind-vector components are interpolated first in time and then by cumulative distance
between the surrounding route anchors. Wind angles themselves are never interpolated. Precipitation
uses the provider's applicable preceding-hour bucket and may be interpolated only spatially between
anchors for that same bucket. An interval is wet when the resulting precipitation is at least the
configured `0.1 mm` threshold.

## Persistence and State

### Training activity additions

`training_activities` gains:

- `weather_state`: `Pending`, `Ready`, `Failed`, or `Unavailable`;
- `weather_provider_version`: nullable stable provider/algorithm identifier;
- `weather_requested_at`: nullable UTC instant;
- `weather_completed_at`: nullable UTC instant;
- `weather_diagnostic_code`: nullable bounded safe code; and
- denormalized display summaries for minimum/maximum temperature, maximum wind speed, prevailing wind
  direction, and precipitation total.

Existing rows migrate to `Pending`. New locatable rows start as `Pending`; rows proven not to contain a
usable time/location domain start as `Unavailable` with a stable code.

### Historical observations

A new `activity_weather_observations` table contains:

- activity ID;
- anchor sequence and cumulative route distance;
- requested latitude/longitude;
- returned grid latitude/longitude and elevation when supplied;
- valid UTC hour;
- temperature Celsius;
- surface pressure hPa;
- precipitation mm;
- wind speed m/s;
- wind direction degrees;
- provider version; and
- retrieval time.

The unique key is `(activity_id, anchor_sequence, valid_at)`. Re-enrichment replaces one activity's
complete observation set and summary in a transaction. Readers can therefore see either the previous
complete version or the new complete version, never a partial series.

Deleting a training activity cascades to its observations through the existing activity deletion
transaction. Historical observations are included in normal database backup and restore.

### Model algorithm version

`BuildModelJobHandler.AlgorithmVersion` is bumped to a stable weather-aware version. A successful model
snapshot continues to store reference calm/dry coefficients and the existing reference air density;
historical interval density is an input to calibration, not a new ambient condition baked into the
snapshot.

## Enrichment Jobs and Backfill

`JobType` gains `EnrichTrainingWeather`. Its handler reports progress for loading the activity,
selecting anchors, fetching weather, validating weather, saving weather, and queueing a model rebuild.

Provider timeouts, `429`, `5xx`, and incomplete transient responses use the existing bounded worker
retry policy. Earlier attempts keep the activity `Pending`. When the final attempt is exhausted, the
handler records `Failed` with a safe diagnostic before the job becomes terminal. A permanent activity
or response defect records `Unavailable` and fails permanently without further provider calls. In both
terminal cases, a coalesced model build may proceed using other Ready evidence.

A startup reconciler runs after database migrations are ready. In bounded batches it enqueues one
weather job for each `Pending`, stale-provider-version, or `Failed` activity that does not already have
an active weather job. Existing job uniqueness and the observation key make
reconciliation safe after restarts or partial deployment. The reconciler does not wait for enrichment
and does not delay readiness.

A model-build job does not replace the current model while any otherwise-eligible activity is still
`Pending`; it exits successfully after reporting that enrichment is outstanding. Each weather job
coalesces a successor build, so the last pending activity reaching `Ready`, `Failed`, or `Unavailable`
causes the final backfill model to be built. `Failed` and `Unavailable` activities are counted and
reported but excluded. This avoids briefly replacing a good legacy model with models learned from
progressively larger partial subsets.

For a later individual upload, the existing current model remains active until that ride reaches a
terminal weather state and a successor model build completes.

## Weather-Aware Model Interpretation

### Typical-power model

`PowerModelBuilder` continues to consume measured watts with the current duration weighting,
per-activity caps, weighted medians, shrinkage, and confidence thresholds. Weather state determines
whether the activity is eligible to reach the builder, but weather values do not scale or reweight
power samples.

This is deliberately conservative. Heat and cold can affect real performance, but converting one
ride's measured watts into a hypothetical thermoneutral value would require rider-specific acclimation,
hydration, clothing, exposure duration, and intent that the application does not know.

### Air density

For each interval, dry-air density is calculated from surface pressure and absolute temperature:

```text
rho = pressurePa / (specificGasConstantDryAir * temperatureKelvin)
```

Humidity is outside scope. The calculation validates positive pressure and absolute temperature and
returns no value rather than substituting the model's reference density when input is invalid.

### Wind vectors and longitudinal aerodynamic force

Open-Meteo direction is meteorological: degrees clockwise from north describing where wind comes from.
It is converted to an east/north vector describing where the air is moving. Consecutive ride or route
positions supply the rider's unit heading.

For rider ground-velocity vector `vGround` and wind-to vector `vWind`:

```text
vAir = vGround - vWind
longitudinalAeroBasis = 0.5 * rho * length(vAir) * dot(vAir, riderHeading)
longitudinalAeroForce = CdA * longitudinalAeroBasis
```

This retains the sign of the along-course force, including a tailwind faster than the rider. The model
does not attempt a yaw-dependent CdA. Non-finite geometry or weather excludes the interval rather than
falling back to still air.

`CyclingForces` gains an explicit environment-aware longitudinal aerodynamic operation. The existing
still-air overload remains and delegates to the new operation with a zero wind vector, preserving the
ordinary predictor path.

### Physics calibration

`PhysicsCalibrator` receives weather-resolved activities. It keeps the existing sample validity rules,
robust Huber fit, coefficient bounds, conditioning checks, coverage thresholds, and fallback codes.
Its aerodynamic regression basis changes from fixed-density ground-speed squared to the interval's
signed apparent-wind basis.

An interval at or above the precipitation threshold is excluded from the entire CdA/Crr fit. That is
necessary because the two-parameter fit cannot separate wet-surface effects from dry Crr while sharing
one Crr coefficient. The resulting persisted Crr and CdA therefore remain dry, calm-reference rider
properties suitable for ordinary predictions.

### Descent limits

Descent evidence remains grouped by grade and curvature, with the existing duration/activity thresholds,
P90 statistic, shrinkage, hard caps, and conservative fallback. Before an interval contributes:

- it must be dry;
- its crosswind component must not exceed a configurable default of `3 m/s`;
- discontinuity and existing geometry/speed rules must pass; and
- its speed must be either effectively calm already or normalizable from sufficient physics evidence.

For a modest-wind, power-bearing interval, a bounded numerical solve finds the calm-air speed that,
with the calibrated dry Crr/CdA, recorded grade, recorded rider power, and observed acceleration, would
produce the same longitudinal force balance. A low-wind interval may use observed speed directly. An
interval with missing required power, no finite bounded solution, an overpowering tailwind, or strong
crosswind is omitted. This converts defensible headwind/tailwind evidence while declining to invent a
handling model for crosswind.

`BuildModelJobHandler` therefore calibrates physics before calling the weather-aware descent builder.
If weather filtering leaves inadequate evidence, the existing conservative descent cell wins and its
confidence remains low.

### Leave-one-ride-out validation

Each validation fold trains its power, physics, and descent components from Ready training activities
other than the held-out ride. It reconstructs the held-out route and runs the environment-aware
predictor with that ride's historical weather resolver. The predicted moving time is compared with the
recorded moving time as before.

This measures whether a calm-reference rider model can explain a real ride once the actual environment
is supplied. It does not penalize a model merely because a held-out ride had a headwind, unusual air
density, or dry-versus-wet mismatch. A fold without complete weather or a valid environmental solution
is skipped under the existing not-validated semantics.

## Prediction Simulation Boundary

`IRoutePredictor.Predict` gains an optional provider-neutral environmental resolver. With no resolver,
it uses the current reference air density, zero wind, and dry conditions. Every ordinary prediction
and pacing adjustment continues to omit the resolver, preserving their behavior.

The resolver receives route segment geometry, course bearing, and absolute simulated time and returns:

- air density;
- wind east/north components;
- precipitation and wet/dry state; and
- environment confidence/provenance needed for warnings.

The predictor uses signed longitudinal aerodynamic force during every integration substep. It applies
the wet descent factor after the existing learned or conservative descent cap is resolved. A wet
segment's confidence is lowered by one level, with Low as the floor, and the route receives one stable
wet-weather warning regardless of how many segments are wet.

Target power is still obtained from the captured power model and any existing explicit power policy.
The environment never changes target watts. The weather-download path supplies no pacing-adjustment
policy.

## Route-Time Forecast Download

### HTTP and UI contract

The existing routes remain:

```http
GET /api/predictions/{id}/gpx
GET /api/predictions/{id}/gpx?timed=true
```

The opt-in variant is:

```http
GET /api/predictions/{id}/gpx?timed=true&weather=current
```

`weather=current` without `timed=true` is a bad request because weather changes predicted timing, not
route geometry. Unknown weather values are also rejected rather than ignored.

The prediction-detail page keeps both existing download actions. A checkbox labelled “Adjust for
current weather” is associated only with “Download GPX with predicted times.” When clear, the existing
anchor behavior is unchanged. When selected, the client fetches the opt-in URL, shows a busy state,
downloads the returned blob under a weather-adjusted filename, revokes the object URL, and reports
Problem Details inline on failure. The raw GPX button, Garmin controls, and adjustment UI do not read
the checkbox.

### Transient recomputation

The export service:

1. requires a succeeded prediction with retained GPX, captured profile, captured rider-model snapshot,
   and a weather-aware model algorithm version;
2. parses/processes the retained GPX to recover the complete route and first-segment bearing;
3. captures `TimeProvider.GetUtcNow()` once as the ride start;
4. uses stored baseline cumulative times to estimate the forecast window;
5. selects the same distance-based route anchors used for historical enrichment;
6. requests forecast variables for the estimated window plus one hour and 50% duration margin;
7. constructs a route/time resolver over the forecast series;
8. runs the route predictor once, letting each segment resolve weather at
   `start + current simulated elapsed time`;
9. extends the forecast once and reruns if the valid simulation exceeds the fetched window; and
10. maps the result into in-memory GPX source segments and writes a timed GPX whose first timestamps
    begin at the captured start.

Forecast duration is bounded by Open-Meteo's supported horizon and application configuration. A route
whose required traversal cannot be covered fails instead of using stale final-hour conditions.

The GPX description identifies the output as forecast-adjusted, includes the forecast retrieval/start
time and a compact condition summary, and includes a wet-conditions warning when applicable. It must
not include provider request URLs or sensitive diagnostics. The suggested filename adds a stable
`-weather-adjusted` suffix.

The service does not update `predictions`, `prediction_segments`, `prediction_adjustments`, jobs, the
rider model, or Garmin course state. A short-lived in-memory cache may coalesce identical provider
requests made within five minutes; it is process-local, bounded, and contains no durable result.

### Legacy models

Physical coefficients learned by the old algorithm may already contain wind and fixed-density bias.
Applying explicit wind to them could double-count that bias. The endpoint therefore rejects a captured
model whose algorithm version predates this feature with a stable conflict response. The UI explains
that the route must be submitted again after weather backfill. Existing raw and baseline timed exports
remain available.

## API Errors and Failure Semantics

The opt-in endpoint uses stable Problem Details codes:

- `prediction-not-found` for an unknown prediction;
- `prediction-not-complete` when no complete route is available;
- `weather-adjustment-requires-timed-gpx` for an invalid option combination;
- `weather-adjustment-unsupported-model` for a legacy captured model;
- `weather-forecast-unavailable` for timeout, rate limit, or provider failure;
- `weather-forecast-incomplete` when required variables or time coverage are missing; and
- `invalid-weather-adjusted-prediction` when the environmental simulation cannot produce a valid
  result.

Provider failure never falls back to calm/dry output under a weather-adjusted request. The original
download links remain the explicit fallback chosen by the rider.

Historical enrichment diagnostics distinguish retryable provider failure, invalid provider data,
unusable activity time/location, and stale provider version. Stored diagnostics are bounded safe codes
and messages. Response bodies, route coordinates, request URLs, API keys, and raw weather payloads are
not logged.

## Configuration

A `Weather` configuration section supplies:

- archive and forecast base URLs;
- optional API key;
- explicit archive and forecast model identifiers;
- HTTP timeout;
- maximum locations per provider request;
- historical reconciliation batch size;
- route-anchor spacing, default `10_000 m`;
- precipitation threshold, default `0.1 mm`;
- strong-crosswind descent threshold, default `3 m/s`;
- wet descent multiplier, default `0.85`;
- forecast duration margin and maximum supported horizon; and
- short-lived forecast-cache size and lifetime.

Startup validates finite ranges and relationships. Invalid configuration fails fast with no worker or
endpoint activation. Algorithm-affecting values are represented in the local weather provider/model
version so a material change can mark historical observations stale and trigger re-enrichment.

## User-Facing Visibility

Training list/detail responses gain weather state and optional summary fields. The UI displays:

- Pending: “Historical weather is being added”;
- Ready: temperature range, prevailing wind direction and speed, maximum wind, and precipitation total;
- Failed: “Historical weather could not be retrieved and this ride is excluded from the rider model”;
  or
- Unavailable: the safe reason the ride cannot be weather-enriched.

Prevailing direction is computed from time-weighted east/north vector components, not an arithmetic
mean of degrees. Summaries are descriptive only and do not expose every persisted anchor.

Model status gains counts for Ready eligible evidence, pending otherwise-eligible rides, and
failed/unavailable otherwise-eligible rides. While initial backfill is pending, it states that the
legacy model remains active. Once the final weather-aware rebuild succeeds, its new algorithm version
and existing validation metrics make completion visible.

## Privacy and Operations

Historical enrichment sends representative training-route coordinates and ride times to the configured
Open-Meteo endpoint. Forecast adjustment sends representative prediction-route coordinates and the
intended immediate traversal window. This third-party disclosure is documented in the runbook and user
privacy guidance.

Deployments that cannot disclose route data to the public service can configure a self-hosted
Open-Meteo-compatible endpoint. Coordinate batching minimizes requests and disclosure while retaining
the resolution needed by the model. Application logs contain activity/prediction IDs, counts, status,
latency, provider version, and safe error codes only.

Operational metrics include:

- pending/failed weather-enrichment count and oldest pending age;
- archive and forecast request count, latency, retry, and failure rate;
- weather observations written per activity;
- activities excluded from model building by weather state;
- adjusted-download attempts, success/failure, and duration; and
- wet-segment share in adjusted downloads, without coordinates.

The deployment runbook covers provider reachability, API-key provisioning when applicable, backfill
progress, retry behavior, database growth, feature disablement, and rollback. Disabling provider calls
leaves existing stored observations and the current rider model intact; it disables new weather-aware
model evidence and adjusted downloads without damaging baseline predictions.

## Testing

All automated tests use fake `IWeatherProvider` responses and a fake `TimeProvider`. No test calls the
live Open-Meteo service.

### Weather and physics unit tests

Tests cover:

- meteorological-from direction to east/north wind conversion at cardinal directions and north
  wraparound;
- rider bearings and headwind, tailwind, crosswind, and faster-than-rider tailwind;
- vector interpolation across `359°`/`1°` without an erroneous southerly result;
- dry-air density from Celsius and hPa, including invalid bounds;
- hourly precipitation bucket selection and the exact `0.1 mm` threshold;
- route-distance and time interpolation at boundaries and discontinuities;
- signed longitudinal aerodynamic force reducing exactly to the existing still-air formula;
- synthetic calibration recovering known Crr/CdA under varied wind and temperature;
- wet-interval exclusion and insufficient-evidence fallback;
- calm-equivalent descent normalization, strong-crosswind exclusion, and unsolved-interval omission;
- wet descent multiplier, confidence reduction, and single-warning behavior; and
- unchanged typical-power bands for the same recorded watts under different weather.

### Workflow and persistence tests

Tests prove:

- a parsed upload saves one pending activity and queues weather enrichment, not an immediate model
  build;
- ready enrichment atomically replaces observations, updates summaries, and queues a build;
- retry, final failure, stale-version reconciliation, and restart reconciliation are idempotent;
- duplicate jobs and observations are prevented;
- deletion cascades through observations and retains current upload-deletion behavior;
- pending eligible activities prevent a partial replacement model;
- failed/unavailable activities are counted and excluded once no pending activity remains;
- backfilled activities ultimately produce the new model version;
- PostgreSQL migrations, model snapshot, backup, and restore include the new schema; and
- cancellation before commit leaves no partial weather series.

### Model and prediction tests

Tests prove:

- every leave-one-out fold uses training weather and held-out historical weather correctly;
- the no-environment predictor path preserves existing public outputs, warnings, and confidence;
- ordinary prediction creation still stores calm/dry assumptions and makes no provider request;
- pacing adjustments make no provider request;
- forecast simulation uses route position and evolving simulated elapsed time;
- a route crossing forecast hours changes conditions at the right simulated boundary;
- forecast expansion reruns at most once and incomplete horizon fails cleanly;
- the adjusted GPX begins at the captured request time, has strictly increasing timestamps, contains
  weather metadata, and uses the adjusted filename;
- adjusted export performs no database writes;
- forecast failure does not return baseline bytes;
- legacy captured models receive the stable conflict response; and
- raw GPX, baseline timed GPX, Garmin export, and persisted prediction bytes remain unaffected.

### Client tests

bUnit tests cover checkbox scope, busy/disabled state, successful blob download, object-URL cleanup,
inline Problem Details, legacy-model guidance, and proof that raw GPX, Garmin, and adjustment controls
do not consume weather state.

## Rollout and Rollback

1. Apply the additive schema migration and deploy the weather adapter, enrichment handler, reconciler,
   weather-aware model code, status UI, and guarded adjusted-download endpoint.
2. Keep the current legacy rider model active while the reconciler enqueues bounded historical batches.
3. Observe enrichment age, failure rate, provider latency, and database growth.
4. When no otherwise-eligible ride remains Pending, allow the coalesced build to publish the first
   weather-aware model.
5. Verify its evidence counts, coefficient bounds, validation metrics, and algorithm version.
6. New predictions captured from that model automatically expose the opt-in download checkbox.
7. Existing predictions retain baseline downloads and show the recreate guidance for weather
   adjustment.

Rollback disables new archive/forecast calls, startup reconciliation, and the opt-in endpoint. The
additive weather tables and columns remain so observations are not destroyed. The last valid rider
model and all stored baseline predictions remain usable. A code rollback ignores the additive schema.

The decision-bearing feature pull request must carry the `narrative-required` label and the exact
`## Narrative Context`, `## Narrative Decision`, and `## Narrative Consequences` headings in its body.
Supplying a custom body must preserve those headings because it replaces the repository template.

## Acceptance Criteria

1. Every existing locatable training activity is reconciled through Open-Meteo archive enrichment, and
   completion produces a new weather-aware rider-model version.
2. Every future locatable training upload is weather-enriched before it can influence a rider model.
3. Recorded watts and immutable cleaned activity samples are unchanged by enrichment.
4. Physics calibration uses interval air density and signed apparent wind and excludes wet intervals.
5. Descent learning produces a dry, calm-reference model and falls back conservatively when weather
   filtering removes too much evidence.
6. Leave-one-ride-out validation replays held-out rides in their historical weather.
7. Ordinary prediction and pacing-adjustment paths remain calm/dry and make no weather request.
8. Selecting current weather for timed download uses a route-time forecast beginning at request time,
   reruns the captured model in memory, and returns recalculated strictly increasing GPX timestamps.
9. Forecast rain applies only the approved wet descent behavior and warning, never a power or Crr
   multiplier.
10. Weather-adjusted download creates no durable result and does not change the rider model, prediction,
    Garmin state, or pacing adjustments.
11. Provider failure is visible and never silently produces calm/dry output for an adjusted request.
12. Legacy predictions retain normal downloads but cannot apply explicit weather to legacy physical
    coefficients.
13. Training and model-status UI make pending, ready, failed, unavailable, and excluded evidence
    visible.
14. Automated tests are deterministic and make no live provider calls.

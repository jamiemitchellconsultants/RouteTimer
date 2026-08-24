# RouteTimer Application Design

**Date:** 2026-08-24  
**Status:** Approved in design review; awaiting review of this written specification

## 1. Purpose

RouteTimer is a private-data, single-rider web application that predicts moving time for a road-cycling route. It learns the rider's typical power output from uploaded Garmin FIT activities, including how typical power varies with gradient and elapsed moving duration. It applies that personal effort model to an uploaded GPX route and converts power to speed with a calibrated cycling-physics model.

The first release is not a race optimiser. It predicts a typical ride in calm, dry conditions, excludes stops and traffic delays, and reports uncertainty when the prediction route differs materially from the training evidence.

## 2. Confirmed Scope

The first release shall:

- support one rider account;
- accept multiple FIT files as training activities;
- require timestamped GPS, elevation, speed, and power records in each usable training activity;
- retain original uploads and parsed data;
- accept one GPX file per prediction route;
- require rider weight and bike/equipment weight;
- model road cycling only;
- assume calm, dry conditions;
- predict moving time only;
- display a route map and distance-aligned elevation, gradient, power, and speed profiles;
- preserve prediction history and the exact model/profile snapshot used for each result;
- validate the model against whole held-out activities, with median moving-time error of 10% or less as the usefulness target;
- run in Docker behind the existing shared LocalAI Caddy ingress;
- authenticate through a new Keycloak realm; and
- use PostgreSQL through a separate persistence project.

The first release shall not model wind, precipitation, traffic, planned stops, gravel/off-road surfaces, racing effort, pacing optimisation, multiple riders, social features, or live ride tracking.

## 3. Source-File Decision

Garmin's GPX export of the supplied activity contains timestamps, position, elevation, heart rate, cadence, and temperature but no power values. Training uploads must therefore be FIT files. FIT preserves the recorded power-meter stream and the timer/session events needed to distinguish riding from pauses.

GPX remains the prediction-route format. A prediction route is expected to contain ordered positions and elevations but does not need timestamps or power.

## 4. Technical Architecture

The solution targets .NET 10 and contains these production projects:

- `RouteTimer.Client`: standalone Blazor WebAssembly application.
- `RouteTimer.Contracts`: API request, response, status, and problem contract types shared by the client and API.
- `RouteTimer.Domain`: dependency-free entities, value objects, physical units, and core calculation rules.
- `RouteTimer.Services`: use cases, parsing/model/prediction orchestration, repository interfaces, and background-job handlers.
- `RouteTimer.Persistence`: EF Core and Npgsql implementations, database mappings, migrations, and durable job leasing.
- `RouteTimer.Api`: ASP.NET Core API, authentication/authorization, upload boundary, dependency composition, health endpoints, and hosting of the compiled client assets.

Dependencies point inward. `Client` depends only on `Contracts`. `Services` depends on `Domain`; `Persistence` implements interfaces owned by `Services` and depends on `Domain`; `Api` is the composition root and may reference `Contracts`, `Services`, and `Persistence`. Neither `Domain` nor `Services` depends on ASP.NET Core, EF Core, or the UI.

The standalone WASM project is compiled independently and copied into the API image during the multi-stage Docker build. ASP.NET Core serves those static assets and the `/api` routes from one origin. This avoids CORS, a second application container, and an additional ingress rule while preserving separate front-end and API projects.

## 5. Authentication and Authorization

Deployment creates an isolated Keycloak realm named `routetimer`. It contains:

- a public SPA client using OpenID Connect authorization code flow with mandatory S256 PKCE;
- an API audience named `routetimer-api`;
- a `rider` realm role required by application endpoints; and
- no self-registration.

The single rider is created or assigned the role administratively. Credentials and initial passwords are never committed.

The Blazor client redirects unauthenticated users to Keycloak and requests an access token for the API audience. The API validates the configured issuer, audience, signature, lifetime, and `rider` role. Caddy terminates TLS and reverse-proxies traffic but performs no application authentication. Only liveness and readiness endpoints may be anonymous, and they expose no sensitive configuration.

## 6. Persistence Model

PostgreSQL is the authoritative store. The core records are:

- `RiderProfile`: current rider weight, bike/equipment weight, and modification time.
- `StoredUpload`: original filename, kind, media type, byte length, SHA-256 digest, original bytes, and upload time. The digest and kind form the duplicate boundary.
- `TrainingActivity`: source upload, device/session metadata, start/end time, distance, ascent, moving duration, parse state, summary values, and warnings.
- `ActivitySample`: activity and sequence, timestamp, position, elevation, distance, moving duration, speed, power, optional heart rate/cadence, derived gradient/curvature, and usability flags.
- `AnalysisJob`: job type, target identifier, state, progress, attempt count, lease information, timestamps, and safe diagnostic.
- `RiderModel`: immutable model version, algorithm version, creation time, profile snapshot, calibrated physical coefficients, training coverage, validation metrics, and status.
- `PowerBand`: model version, gradient interval, elapsed-duration interval, robust typical power, evidence duration, contributing activity count, shrinkage weight, and confidence.
- `Prediction`: source GPX upload, rider-model version, profile/assumption snapshot, state, total distance/ascent/moving time, averages, confidence, warnings, and timestamps.
- `PredictionSegment`: prediction and sequence, position/elevation, segment distance, gradient, curvature, predicted power/speed/time, cumulative values, and confidence.

Raw uploads are stored in PostgreSQL rather than a separate object store because this is a single-rider deployment with small activity files. This produces one backup boundary and avoids another service. Parsed time-series data is retained so model rebuilds do not depend on decoder behaviour changing, while the original files remain available for a deliberate reparse migration.

Prediction segments use ordinary numeric latitude/longitude columns; PostGIS is not required for ordered route simulation or map display.

## 7. Training Ingestion and Cleaning

The API accepts a configurable multi-file FIT upload, initially limited to 50 MB per file. It computes a SHA-256 digest while streaming, stores each unique file, creates parse jobs in one transaction, and returns `202 Accepted` with a result for every file. A batch may partially succeed: valid files are accepted while duplicates and invalid files receive their own outcomes.

The FIT decoder is behind a `Services` interface and is implemented with a C# FIT decoder compatible with Garmin's FIT protocol. It reads timer/session events and record messages. A usable activity must contain at least 10 minutes of moving time, with monotonically orderable timestamps and GPS position/elevation/speed present for at least 95% of that time and recorded power present for at least 80%. Individual incomplete samples are excluded rather than imputed. Missing optional heart rate, cadence, or temperature never invalidates an activity.

Cleaning follows these rules:

1. Sort records by timestamp and remove exact duplicates.
2. Use FIT timer events to exclude paused intervals. If timer events are missing, treat samples below 1.0 m/s as stopped for moving-time analysis.
3. Break continuity across timestamp gaps longer than 10 seconds so distance, acceleration, and gradient are not inferred through missing data.
4. Exclude absent or physically implausible coordinates, elevation, speed, and power. Recorded zero power remains valid coasting data; an absent power value is not converted to zero.
5. Calculate route distance from coordinates and resample continuous riding sections to 25-metre intervals.
6. Smooth elevation with a distance-based robust local fit over a 100-metre window, using a one-sided window near section ends.
7. Derive gradient from the smoothed elevation and horizontal distance. Values outside -20% to +20% are retained for display, excluded from coefficient fitting, and flagged as questionable evidence.
8. Derive curvature from changes in route heading over distance for descent-speed modelling.

The cleaner records exclusion counts and reasons. The UI exposes a concise quality summary rather than silently discarding evidence.

## 8. Personal Typical-Power Model

For this release, “duration” means elapsed moving time within the ride. The personal power model answers: given the current gradient and how long the rider has already been moving, what power does this rider typically produce?

Clean moving evidence is grouped into these initial gradient bands:

- below -6%;
- -6% to below -3%;
- -3% to below -1%;
- -1% to below +1%;
- +1% to below +3%;
- +3% to below +6%;
- +6% to below +9%; and
- +9% and above.

Elapsed moving-duration bands are 0–30, 30–60, 60–120, 120–180, and 180+ minutes. Boundaries are algorithm configuration and are recorded with the model version.

Each two-dimensional cell uses robust medians rather than means. Evidence is measured by contributing moving duration and distinct activities, not raw one-second sample count. Sparse cell estimates shrink toward gradient-only, duration-only, and rider-wide medians. Values interpolate across adjacent band centres to prevent artificial steps. Extrapolation uses the nearest supported value and lowers prediction confidence. Initial cell coverage is high with at least 15 minutes from three activities, medium with at least 5 minutes from two activities, and low otherwise; these thresholds are recorded with the algorithm version.

The model represents normal observed effort, including low-power descending and recovery behaviour. It does not use maximum power-duration records and does not claim to estimate race-best sustainable power.

## 9. Physical Calibration and Speed Simulation

Total system mass is rider weight plus bike/equipment weight. The simulation accounts for:

- gravity along the slope;
- rolling resistance;
- aerodynamic drag;
- drivetrain efficiency; and
- changes in kinetic energy between route segments.

The wheel-power balance is based on:

`Pwheel = (gravity force + rolling force + aerodynamic force + inertial force) × speed`

with `Pwheel = rider power × drivetrain efficiency`.

Initial conservative road defaults are drivetrain efficiency 0.97, air density 1.225 kg/m³, rolling coefficient 0.005, and CdA 0.32 m². Model construction attempts a bounded robust fit of CdA and rolling resistance from steady, valid historical samples using the profile mass. Fitting excludes stops, braking/coasting descents, implausible gradients, abrupt acceleration, missing power, and discontinuities. CdA is constrained to 0.15–0.60 m² and rolling resistance to 0.002–0.012. If the fit is poorly conditioned or lacks coverage, defaults are retained and confidence is reduced.

The GPX route is validated, split at discontinuities, resampled to 25-metre segments, and processed with the same elevation/gradient/curvature methods used for training. Simulation advances sequentially. At each segment it:

1. selects typical rider power from gradient and cumulative predicted moving time;
2. calculates resistive forces and the energy available to accelerate or maintain speed;
3. advances speed and time over the segment with numerical substeps no longer than one simulated second; and
4. applies learned typical descending/braking limits based on gradient and curvature.

When historical descent coverage is insufficient, conservative grade/curvature caps replace learned limits and are identified in the result. Speed and time must remain finite and non-negative; failure to find a physical solution fails the prediction rather than emitting invalid output.

## 10. Model Building and Validation

Creating, deleting, or reparsing training activities queues a model rebuild. A rebuild creates an immutable new model version; it never mutates a version already referenced by a prediction. Only one rebuild may run at once.

With at least three valid training activities, validation uses leave-one-activity-out testing. For each fold, the system trains without one complete activity, predicts that activity's recorded route from its start, and compares predicted moving time with recorded moving time. It reports per-activity absolute percentage error, median absolute percentage error, and 90th-percentile absolute percentage error. The agreed primary usefulness target is median absolute percentage error of 10% or less.

Fewer than three activities produce an “insufficient validation data” status rather than a passing score. A model that misses the target remains available for predictions, but the dashboard and results show the failed validation status and lower confidence.

Coverage confidence combines distinct activity count, evidence duration in the relevant gradient/duration regions, use of calibrated versus default physical coefficients, and whether the prediction extrapolates beyond training. Route confidence is high only when at least 80% of predicted moving time uses high-coverage cells and calibrated coefficients; medium when at least 80% uses medium-or-better cells; and low otherwise or whenever default coefficients dominate. Confidence is shown with its reasons and is not presented as a statistically calibrated probability.

## 11. Prediction Workflow and Result

Creating a prediction requires a ready rider model and a complete rider profile. The API stores the GPX, creates a durable job, and returns `202 Accepted`. The client follows job progress and navigates to the completed result.

The result includes:

- distance, ascent, predicted moving time, average speed, and average predicted power;
- model version, rider/bike mass snapshot, road/calm/dry/moving-only assumptions, and creation time;
- overall confidence, model-validation status, and explicit warnings;
- a route map; and
- synchronized distance-based elevation, gradient, predicted-power, and predicted-speed profiles.

Hovering or selecting a position in a chart highlights the corresponding map location; selecting the route updates the chart cursor. The tile URL and attribution are deployment configuration so the operator can choose a compliant provider.

## 12. API Surface

The authenticated API is resource-oriented:

- `GET` and `PUT /api/profile` read and update rider/bike weights.
- `GET` and `POST /api/training-activities` list activities and upload one or more FIT files.
- `GET` and `DELETE /api/training-activities/{id}` inspect or delete an activity.
- `GET /api/models/current` returns readiness, coverage, calibration, and validation status.
- `POST /api/models/rebuild` requests an explicit rebuild.
- `GET` and `POST /api/predictions` list predictions and upload a GPX route.
- `GET` and `DELETE /api/predictions/{id}` read or delete a prediction.
- `GET /api/jobs/{id}` returns safe progress and diagnostics.
- `GET /health/live` and `GET /health/ready` provide anonymous container health checks without sensitive details.

Uploads use multipart form data. Responses use contract DTOs, never persistence entities. Errors use RFC-style problem details with stable application error codes. Invalid user input is not retried; transient job failures use a bounded retry policy.

## 13. Durable Background Jobs

`RouteTimer.Api` hosts a background worker; a separate worker container is unnecessary for the single-rider load. Jobs live in PostgreSQL and are claimed transactionally with a lease. A worker renews the lease during long operations. An expired lease makes interrupted work eligible for safe retry after container restart.

Jobs are idempotent and use staging state before replacing parsed samples or publishing a completed model/result. Permanent validation failures move directly to `failed`. Transient database or process failures retry with backoff up to three attempts. Safe diagnostics are persisted; stack traces remain in structured server logs.

A multi-file upload may create parallel parse records, but model rebuilds are coalesced so the batch produces one rebuild after its accepted parses settle. Predictions always capture the ready model version at job creation, preventing a concurrent rebuild from changing their meaning.

## 14. User Interface

The client contains four main areas:

- **Dashboard:** current model status, validation error, coverage warnings, recent predictions, and a prominent new-prediction action.
- **Training:** multi-file upload, per-file state, activity summaries, quality warnings, delete action, and model-rebuild progress.
- **Rider profile:** rider weight and bike/equipment weight with units and validation.
- **Predictions:** GPX upload, job state, prediction history, and the detailed result view.

The interface remains usable during background work and survives a page reload by retrieving job state from the API. Empty states explain that FIT rather than activity GPX is required for power analysis. Destructive delete actions require confirmation and explain that deleting training data triggers a model rebuild.

## 15. Failure Handling

Jobs use queued, running, succeeded, failed, and cancelled states with timestamps and progress. User-visible failures fall into four categories:

- invalid or unsupported FIT/GPX input;
- insufficient training evidence;
- model-quality or extrapolation warnings; and
- operational database, authentication, worker, or deployment failures.

Low confidence does not prevent a prediction when conservative defaults produce a physical result. It must state which defaults or unsupported regions were used. Corrupt input, missing required power evidence, unsafe XML, non-finite calculations, and physically impossible solver states fail clearly.

The GPX parser disables DTD and external-entity resolution, enforces the 50 MB upload limit and an initial 250,000-track-point limit, and processes only the expected GPX structures. Logs do not contain uploaded file bytes, tokens, passwords, or full route coordinates at normal levels.

## 16. Docker, Caddy, and Keycloak Deployment

The RouteTimer Compose project contains:

- `routetimer-web`, the ASP.NET Core API plus compiled WASM assets; and
- `routetimer-db`, PostgreSQL with a named volume.

The database joins only a private RouteTimer network and exposes no host port. The web service joins that private network and LocalAI's existing external `mcp-public` network. It also exposes no host port. Caddy reaches it by Docker DNS as `routetimer-web:8080`.

A generated Caddy drop-in maps a deployment-supplied public hostname to `routetimer-web:8080`. It is installed beneath `C:\mcp-host\caddy\conf.d`, validated as part of the complete shared Caddy configuration, and applied with `caddy reload`; the shared ingress is never restarted merely to deploy RouteTimer.

An idempotent Windows deployment script follows the LocalAI repository's established host convention. It:

1. validates Docker, the shared Caddy stack, the `mcp-public` network, Keycloak, required parameters, and secret files;
2. builds and starts the RouteTimer Compose project;
3. applies EF Core migrations with a database advisory lock before readiness is reported;
4. creates or reconciles the `routetimer` realm, SPA client, API audience, role, redirect origins, and logout origins;
5. installs the RouteTimer Caddy drop-in;
6. validates the complete Caddy configuration before reloading it; and
7. verifies public TLS, client loading, OIDC discovery, API readiness, and an authenticated API request.

The public hostname, Keycloak public base URL, PostgreSQL password, and initial rider administration are deployment inputs, not source-controlled values. Compose health checks gate web readiness on PostgreSQL and successful migrations.

## 17. Verification Strategy

Automated verification includes:

- unit tests for FIT/GPX parsing, moving-time selection, smoothing, gradient/curvature, power-band shrinkage/interpolation, coefficient fitting, physical forces, numerical simulation, descent limits, confidence, and finite/non-negative invariants;
- synthetic anonymized FIT and GPX fixtures; the supplied personal files are local exploratory samples and are never committed;
- PostgreSQL integration tests, preferably with a disposable container, for migrations, upload retention, deduplication, repositories, leases, retry/recovery, model immutability, deletion/rebuild behaviour, and prediction snapshots;
- API tests for OIDC authorization policy, role/audience enforcement, multipart limits, safe XML, partial batch outcomes, problem details, and job/result retrieval;
- Blazor component tests for core states and validation;
- browser tests for login, profile setup, training upload, rebuild progress, prediction upload, result display, and synchronized chart/map selection;
- leave-one-activity-out validation with the 10% median-error target; and
- Docker Compose validation, container health checks, complete Caddy validation, and an authenticated public deployment smoke test.

Tests use deterministic tolerances for physics rather than exact floating-point equality. Golden expected values record units and assumptions. A model failing the empirical accuracy target fails the model-quality acceptance report, not unrelated parser or deployment tests.

## 18. Acceptance Criteria

The first release is complete when:

1. An authenticated rider can enter both weights and upload a batch of FIT activities.
2. Originals and parsed samples survive container restart, duplicates are identified, and invalid files have actionable per-file errors.
3. A versioned typical-power/physics model is built and its coverage and validation status are visible.
4. With three or more suitable activities, whole-activity validation reports per-ride and median moving-time error and identifies whether the median is within 10%.
5. The rider can upload an elevation-bearing GPX route and receive a persistent detailed moving-time prediction.
6. The result shows summary metrics, assumptions, confidence/warnings, an interactive map, and synchronized elevation/gradient/power/speed profiles.
7. Deleting training data rebuilds the current model without altering historical prediction snapshots.
8. The app runs in Docker with PostgreSQL private, no application host ports, and traffic entering only through the existing shared Caddy network.
9. The new Keycloak realm protects the API with issuer, audience, lifetime, and rider-role validation.
10. Automated tests, Compose validation, Caddy validation, health checks, and the authenticated deployment smoke test pass.

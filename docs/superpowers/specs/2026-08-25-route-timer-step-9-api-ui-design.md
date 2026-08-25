# RouteTimer Step 9 API and UI Design

## Purpose

Step 9 exposes the completed RouteTimer data, model, and prediction workflows through a stable authenticated API and a usable Blazor WebAssembly interface. It also closes the persistence and service-projection gaps that prevent the UI from showing training quality, durable job progress, model readiness, prediction history, and detailed results.

This design supplements `docs/superpowers/specs/2026-08-24-route-timer-design.md`. The original design remains authoritative when the two overlap.

## Scope

Step 9 includes:

- persisted training-activity session and device summary metadata;
- durable job progress, lifecycle timestamps, and cancellation state;
- per-file accepted-upload identifiers and the approved training resource paths;
- training list, detail, deletion, and rebuild orchestration;
- current-model readiness, coverage, calibration, validation, and rebuild APIs;
- the complete prediction resource API, including deletion;
- a typed client API boundary with centralized problem parsing and job polling;
- dashboard, profile, training, prediction-history, and prediction-detail UI;
- locally bundled Leaflet and Chart.js route visualization;
- synchronized map and elevation, gradient, power, and speed profiles; and
- focused domain, service, persistence, API, client-component, and JavaScript tests.

Step 9 does not change model-building, physical-calibration, descent-limit, route-processing, or sequential-simulation algorithms. It does not add deployment automation, production readiness checks, backup/rollback documentation, authenticated browser tests, or deployment smoke tests; those remain Step 10.

## Implementation Strategy

Implementation proceeds producer before consumer:

1. complete domain and persistence data;
2. expose service queries and commands;
3. define and test API contracts and endpoints;
4. add a typed client boundary and reusable client infrastructure;
5. build page workflows; and
6. add visualization after prediction detail data is stable.

This order is binding for the implementation plan. Each task must have a focused red-green-refactor cycle and an independently reviewable commit.

## Training Metadata

`CleanedActivity` gains an immutable `TrainingActivityMetadata` value containing:

- `StartedAt`;
- `EndedAt`;
- optional device manufacturer;
- optional device product;
- optional device-recorded distance;
- optional device-recorded ascent; and
- the source filename.

`ParsedFitActivity` carries the session and file-identity data needed to construct the metadata. `FitActivityParser` reads canonical manufacturer/product values from the FIT file identity and start/end/distance/ascent values from the FIT session. Missing optional metadata does not invalidate an activity.

Metadata normalization rules are:

- timestamps must be finite UTC `DateTimeOffset` values with `EndedAt >= StartedAt`;
- when the session end is absent, the latest decoded sample timestamp is used;
- distance and ascent must be finite and non-negative or become unavailable;
- manufacturer and product are trimmed canonical strings or `null`; and
- the source filename is the retained upload filename, not the parser's generic activity name.

The training entity stores these values in nullable columns where old rows cannot be reconstructed safely. Existing rows remain readable. Repository mapping must never invent zero distance/ascent or an epoch timestamp for unavailable legacy metadata.

Training summary queries do not return raw samples. Detail queries return metadata, moving duration, eligibility, coverage, exclusion counts, and reason codes only.

## Durable Job Progress and Lifecycle

`JobState` contains exactly `Queued`, `Running`, `Succeeded`, `Failed`, and `Cancelled`.

Each persisted job stores:

- progress percentage in the inclusive range `0..100`;
- a stable progress-stage code;
- `CreatedAt`;
- optional `StartedAt`;
- `UpdatedAt`;
- optional `CompletedAt`;
- attempt count, worker ownership, and lease expiry; and
- safe diagnostic code/message.

Queue transition rules are:

- enqueue: `Queued`, `0`, stage `queued`, all lifecycle values derived from the injected `TimeProvider`;
- claim: `Running`, preserve `CreatedAt`, set `StartedAt` only on the first claim, update `UpdatedAt`, and preserve monotonic progress across an expired-lease retry;
- handler progress: only the owning worker may update a running job; percentage may remain equal or increase but may never decrease, and handlers may report only `1..99`;
- success: `Succeeded`, `100`, stage `completed`, set `UpdatedAt` and `CompletedAt`, clear lease/worker and diagnostics;
- failure: `Failed`, keep the last non-terminal percentage, stage `failed`, set `UpdatedAt` and `CompletedAt`, clear lease/worker, and store only safe diagnostics;
- cancellation: `Cancelled`, keep the last percentage, stage `cancelled`, set `UpdatedAt` and `CompletedAt`, clear lease/worker, and do not expose an internal exception; and
- terminal rows are immutable except for deliberate resource deletion.

Handlers report coarse stable stages rather than sample-level progress. The required stage codes are:

- parse training: `reading-upload`, `decoding-fit`, `cleaning-activity`, `saving-activity`, `queueing-model-rebuild`;
- build model: `loading-evidence`, `building-power-model`, `calibrating-physics`, `building-descent-limits`, `validating-model`, `saving-model`; and
- predict route: `loading-prediction`, `processing-route`, `simulating-route`, `saving-result`.

Stage percentages are constants owned by each handler and must increase monotonically. The UI treats stage codes as data and maps them to user-facing text.

## Coalesced Rebuild Correctness

Training creation and deletion must always produce a model version built from the eventual retained activity set.

At most one queued and one running `BuildModel` job may exist for the single model subject:

- if a queued rebuild exists, enqueue/coalesce returns its ID;
- if no rebuild is active, enqueue/coalesce creates a queued job;
- if a rebuild is running and training data changes, enqueue/coalesce creates or returns one queued successor; and
- further changes while that successor is queued return the successor ID.

The database uniqueness strategy and PostgreSQL tests must enforce this rule under concurrent callers. This replaces the prior behavior where a change arriving during a running build could be lost.

## Resource Services

Focused services sit between endpoints and repositories:

- `TrainingActivityQueryService` lists summaries and reads one detail.
- `TrainingActivityDeletionService` deletes an activity and its retained FIT upload in one transaction, then obtains the required coalesced rebuild job ID.
- `ModelStatusService` returns profile/training prerequisites, current-model data, and the latest active or failed build job.
- `ModelRebuildService` validates prerequisites and returns the coalesced rebuild job ID.
- `PredictionQueryService` continues to return summaries and ordered detail segments.
- `PredictionDeletionService` cancels the associated queued/running job, deletes prediction segments, prediction row, and retained GPX upload atomically, and leaves referenced rider-model snapshots untouched.

Deletion is idempotent at the service boundary only when explicitly stated by the endpoint contract. The HTTP DELETE endpoints return `204` for a deleted resource and `404` when the resource did not exist.

A running prediction handler may finish computation after cancellation, but cancellation/deletion wins publication: publication must require that the prediction row still exists and its job remains owned/running. A cancelled/deleted prediction can never be recreated or published by a late worker.

## API Surface

All application endpoints require the authenticated `rider` role. Only health endpoints remain anonymous.

The Step 9 API surface is:

- `GET /api/profile`;
- `PUT /api/profile`;
- `GET /api/training-activities`;
- `POST /api/training-activities`;
- `GET /api/training-activities/{id}`;
- `DELETE /api/training-activities/{id}`;
- `GET /api/models/current`;
- `POST /api/models/rebuild`;
- `GET /api/predictions`;
- `POST /api/predictions`;
- `GET /api/predictions/{id}`;
- `DELETE /api/predictions/{id}`; and
- `GET /api/jobs/{id}`.

The obsolete `/api/training/uploads` path is removed after all tests and client callers move to `/api/training-activities`. It is not retained as a permanent alias.

Multipart boundaries enforce one 50 MiB limit per FIT or GPX file. Boundary code rejects oversized streams deterministically and does not rely only on a client-supplied content length.

## API Contracts

Contracts are records in `RouteTimer.Contracts`; they contain no domain, persistence, ASP.NET Core, or UI types.

### Training

`TrainingUploadBatchResponse` contains `IReadOnlyList<TrainingUploadFileResponse> Files`.

Each `TrainingUploadFileResponse` contains:

- `FileName`;
- `Outcome` (`accepted`, `duplicate`, or `invalid`);
- optional `UploadId`;
- optional `JobId`; and
- optional `ErrorCode`.

Accepted results contain both IDs. Invalid results contain neither. Duplicate results contain neither unless the implementation can return both existing identifiers transactionally; it must not return one identifier without the other.

`TrainingActivitySummaryResponse` contains activity ID, upload ID, source filename, optional start/end time, optional device manufacturer/product, optional distance/ascent, moving seconds, eligibility, all four coverage ratios, and creation time. `TrainingActivityDetailResponse` contains the summary plus exclusion counts and reason codes. Numeric values are presentation-neutral and statuses/reasons are stable string codes. Percentages remain `0..1` ratios in the API and are formatted as percentages only in the client.

### Jobs

`JobResponse` contains identity/type/subject, state, progress percentage/stage, attempt count, created/started/updated/completed times, optional running lease expiry, and safe diagnostics. Worker IDs are never returned.

### Models

`ModelStatusResponse` contains:

- `IsReady` and optional stable `BlockingReason`;
- optional current model ID, algorithm version, and creation time;
- calibration and learned-descent flags;
- validation status and optional median/p90 absolute percentage errors;
- physical coefficient values used by the current model;
- power-band coverage rows with evidence, activity count, shrinkage, and confidence;
- learned/fallback descent-cell counts; and
- optional current rebuild `JobResponse`.

A current immutable model remains usable while a successor rebuild is queued or running. Rebuild failure is shown as a warning and does not make an existing current model unavailable.

`ModelRebuildResponse` contains the coalesced `JobId` and returns from `202 Accepted`.

### Predictions

Existing summary, detail, segment, and submission contracts remain the basis of the resource. The client must consume `PredictionSubmissionResponse`; the obsolete preview-only client assumption is removed.

Prediction summaries remain segment-free. Detail segments are ordered by sequence and contain all map/profile values.

## HTTP Status and Problems

Errors use RFC problem details with a stable string `code` extension.

- `400 Bad Request`: malformed request, missing multipart boundary, or invalid scalar request shape;
- `404 Not Found`: requested activity, model/job subject, prediction, or job is absent;
- `409 Conflict`: profile missing, model not ready, no eligible training evidence, or another state conflict prevents the operation;
- `413 Payload Too Large`: a file exceeds the configured limit;
- `422 Unprocessable Entity`: reserved for semantic validation deliberately performed synchronously at an endpoint boundary; and
- `500 Internal Server Error`: a safe generic title/detail with no stack trace or persistence information.

Per-file mixed FIT upload outcomes remain inside the `202 Accepted` response rather than turning one invalid file into a batch-level problem.

FIT decoding and GPX processing remain background operations. Corruption discovered after `202 Accepted` appears as a durable failed job with a safe diagnostic; endpoints do not decode the same file a second time merely to produce `422`. The `422` status is reserved for semantic validation that is deliberately performed synchronously at an endpoint boundary.

The client maps known stable codes to specific guidance and renders a generic retry message for unknown/network failures.

## Typed Client Boundary

`IRouteTimerApiClient` defines one method per API operation. `RouteTimerApiClient` owns:

- relative resource paths;
- JSON serialization/deserialization;
- multipart construction;
- bounded file stream handling;
- RFC problem parsing;
- success-status validation; and
- cancellation propagation.

Pages and presentational components never construct raw API URLs, read raw `HttpResponseMessage` objects, or parse problem JSON.

`ApiProblemException` contains HTTP status, stable code, title, and safe detail. It does not expose raw response bodies.

`JobPoller` requests a job immediately, then every two seconds until `Succeeded`, `Failed`, or `Cancelled`. It uses `TimeProvider` or an injected delay abstraction for deterministic tests, treats repeated `404` as terminal resource removal, and cancels when its owning component is disposed or navigates away.

## Client Pages

### Dashboard `/`

The dashboard loads profile, training summaries, current model status, and recent predictions. It displays:

- profile complete/missing;
- eligible and total activity counts;
- current model ready/building/blocked status;
- validation target and median/p90 results;
- calibration and descent-learning state;
- active rebuild progress or latest rebuild failure;
- recent prediction state/summary; and
- one clear action for each unmet prerequisite.

Independent resource failures do not blank the entire dashboard. Each section has its own loading, empty, warning, and failure state.

### Profile `/profile`

The profile page loads current values when present, accepts rider weight `30..250 kg` and bike/equipment weight `3..60 kg`, prevents invalid or duplicate submission, and shows field-level validation plus safe server problems. Successful save updates the displayed state without a page reload.

### Training `/training`

The training page:

- accepts multiple `.fit` files;
- displays every per-file outcome and links accepted files to their parse jobs;
- polls active parse and rebuild jobs with disposal cancellation;
- lists activities newest first;
- shows eligibility and concise quality reasons;
- links to detail; and
- requires confirmation before deletion, explaining that deletion removes retained evidence and queues a model rebuild.

### Training detail `/training/{id:guid}`

The detail page shows filename, activity/session/device metadata, distance/ascent/moving time, coverage ratios, eligibility, exclusion counts, and stable reason descriptions. It does not fetch or render raw activity samples.

### Predictions `/predictions`

The predictions page:

- shows model/profile prerequisite guidance;
- accepts exactly one `.gpx` file;
- consumes `PredictionSubmissionResponse` from `202 Accepted`;
- polls the returned job;
- navigates to completed detail;
- lists durable history newest first; and
- confirms deletion.

### Prediction detail `/predictions/{id:guid}`

The detail page shows:

- distance, ascent, moving time, average speed, and average power;
- prediction/model identifiers and timestamps;
- rider/bike mass snapshot;
- road/calm/dry/moving-only assumptions;
- model validation state;
- confidence and warnings; and
- map and four synchronized profiles when ordered segments exist.

Units are formatted only in the client: kilometres, metres, `h:mm:ss`, kilometres per hour, watts, kilograms, percentages, and localized timestamps.

## Shared Client Components

Reusable components are limited to behavior genuinely shared by pages:

- `ProblemMessage` renders safe actionable errors;
- `JobProgress` renders state, progress, stage text, retry/failure diagnostics, and terminal status;
- `ModelStatus` renders readiness/calibration/validation/coverage summary;
- `ConfidenceBadge` maps stable confidence values to text and styling; and
- formatting helpers centralize units and duration/percentage rendering.

Pages own orchestration state. The design does not add a global state store.

Every asynchronous page renders explicit loading, empty, queued/running, success, warning, failed, and cancelled states where those states apply.

## Route Visualization

Leaflet and Chart.js are exact-version npm dependencies recorded in `package-lock.json`. A deterministic vendor build copies only required distributable JavaScript, CSS, and image assets to `wwwroot/vendor`. Runtime pages load no CDN resources.

`wwwroot/appsettings.json` supplies tile URL and mandatory attribution text. Missing either value produces a visible configuration problem and skips map initialization.

The visualization implementation is split into:

- `RouteMap.razor`, which owns the map container, selected sequence, and .NET callback;
- `RouteProfiles.razor`, which owns aligned profile canvases and selected sequence;
- `route-visualization.js`, which owns Leaflet/Chart.js handles by unique component ID; and
- pure JavaScript helpers for dataset construction and nearest-segment selection.

All profiles use cumulative distance as the x-axis. Elevation, gradient, predicted power, and predicted speed retain their own units/scales.

Chart hover and map click exchange segment sequence numbers through `DotNetObjectReference`. Selection moves a single map marker and a single chart cursor. Nearest-segment lookup is deterministic and resolves equal distances to the lower sequence.

Components initialize only after successful prediction detail with non-empty ordered segments. They dispose every JavaScript handle and `DotNetObjectReference`; repeated navigation cannot retain old maps, charts, or callbacks.

## Accessibility and Presentation

The existing Bootstrap foundation remains. Step 9 adds focused component/page CSS rather than a new UI framework.

- all inputs have labels and validation associations;
- loading/status changes use appropriate live-region roles without excessive announcements;
- progress has text in addition to visual bars;
- confidence and job states are never communicated by color alone;
- keyboard users can reach every action and prediction-history link;
- tables have headings and collapse or scroll safely on narrow viewports; and
- map/profile selection has a textual selected-distance/metrics readout.

## Testing

### Domain and services

Tests cover metadata normalization, query projections, prerequisite derivation, rebuild coalescing, deletion orchestration, cancellation/publication races, problem-code mapping, and deterministic polling.

### Persistence

Real PostgreSQL tests cover:

- migration from the current schema with legacy rows;
- training metadata round trip and legacy-null mapping;
- job lifecycle/progress invariants;
- concurrent rebuild coalescing with one running plus one queued successor;
- activity/source deletion;
- prediction/job/upload atomic cancellation and deletion; and
- late publication rejection after cancellation/deletion.

EF pending-model verification must report no changes after the migration.

### API

Authenticated integration tests cover every verb/path, response DTO shape, `202/204/400/404/409/413` behavior, mixed FIT batch outcomes, authorization, stable problem codes, ordered prediction detail, and removal of the obsolete upload path. No Step 9 upload endpoint emits `422` by decoding content twice; the status remains reserved for a future synchronous semantic boundary.

### Client

bUnit tests use a fake `IRouteTimerApiClient`, not raw HTTP handlers. Tests cover loading/empty/failure states, validation, per-file outcomes, polling cancellation, deletion confirmation, model guidance, prediction navigation, formatted detail metrics, warning/confidence text, and interop initialization/disposal.

### JavaScript

Node's built-in test runner covers pure dataset construction, unit conversion inputs, nearest-segment selection including tie behavior, empty data, and selection synchronization messages. Browser-library rendering itself is verified by the deterministic vendor build in Step 9 and the authenticated browser test in Step 10.

## Verification Gate

Before integration:

1. run focused tests after each task;
2. run `npm ci` and the vendor build;
3. run Node JavaScript tests;
4. run the complete .NET solution tests in a clean single-worker process;
5. run the formatter in verify mode;
6. run `git diff --check`;
7. run EF pending-model verification;
8. verify no CDN URL is present in runtime client assets;
9. verify the obsolete `/api/training/uploads` route has no callers; and
10. perform a whole-branch review against this design and the authoritative RouteTimer design.

## Acceptance Criteria

Step 9 is complete when:

1. an authenticated rider can inspect activity quality and metadata, upload multiple FIT files, follow parse/rebuild progress, and delete evidence safely;
2. model readiness, calibration, coverage, validation, active rebuild, and safe failure state are visible through the API and UI;
3. profile editing enforces the approved ranges and reports safe actionable errors;
4. an authenticated rider can submit a GPX, follow durable progress, reload prediction history, inspect detailed results, and delete a prediction;
5. prediction detail shows the exact stored model/profile/assumption snapshot and explicit confidence/warnings;
6. map and four profiles are locally bundled, distance-aligned, synchronized, accessible, and correctly disposed;
7. durable job progress/lifecycle transitions and training-triggered rebuild coalescing remain correct under PostgreSQL concurrency;
8. mixed upload results and all resource errors use the approved status and stable contracts;
9. all focused and full verification checks pass; and
10. deployment hardening and authenticated browser acceptance remain clearly deferred to Step 10.

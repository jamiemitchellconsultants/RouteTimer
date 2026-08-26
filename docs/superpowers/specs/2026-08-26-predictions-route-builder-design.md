# Predictions Route Builder, GPX Export, and Garmin Course Push Design

**Date:** 2026-08-26
**Status:** Approved in conversational design review; awaiting review of this written specification

## Purpose

RouteTimer shall let its authenticated rider start a prediction from a Google Maps route as well as
from a GPX upload, save a Google Maps API key for reuse, download any completed prediction as a GPX
file, and push that GPX to Garmin Connect as a course.

The Google Maps capability is a port of the existing MapToGarmin application
(`~/RiderProjects/MapToGarmin`), adapted to RouteTimer's API-backed architecture and to the needs of
a physics prediction rather than a navigation export.

This feature supplements `docs/superpowers/specs/2026-08-24-route-timer-design.md`,
`docs/superpowers/specs/2026-08-25-route-timer-step-9-api-ui-design.md`, and
`docs/superpowers/specs/2026-08-25-garmin-activity-import-design.md`. Those specifications remain
authoritative where this document does not explicitly extend them.

## Confirmed Scope

The feature shall:

- add a second route input mode to the Predictions page: a Google Maps URL plus a Google Maps API
  key, alongside the existing GPX upload;
- accept both full `https://www.google.com/maps/dir/...` URLs and short `https://maps.app.goo.gl/...`
  links;
- build the GPX in the browser from Google's Directions and Elevation JavaScript services, and submit
  it through the existing prediction upload endpoint;
- expand short links through a new RouteTimer API route rather than through ingress configuration;
- let the rider save one Google Maps API key, encrypted at rest with AES-GCM, and delete it again;
- export any completed prediction as a GPX file, in an untimed course variant and a timed variant;
- push a completed prediction to Garmin Connect as a course through the existing Python adapter and
  the existing stored Garmin session; and
- record the resulting Garmin course identifier against the prediction.

The feature shall not:

- introduce a second prediction submission path, job type, or route processing pipeline;
- send the Google Maps API key, the Google Maps URL, or route geometry to any RouteTimer server
  component other than the completed GPX that the prediction endpoint already accepts;
- use Garmin's official partner Courses API;
- generate course turn points, cue sheets, or PacePro strategies;
- introduce per-user scoping for the new stored key or for any existing entity; or
- contact Google or Garmin from automated tests or CI.

## Relationship to MapToGarmin

MapToGarmin is a standalone Blazor WebAssembly application with no API of its own. RouteTimer has a
first-party API, a persistence layer, an authenticated rider, and a physics model that consumes route
gradients. Three of MapToGarmin's design decisions therefore change on the way across, and the rest
of its code ports substantially unchanged.

### Ported substantially unchanged

`GoogleMapsUrlParser`, `MapUrlParseException`, the `ParsedRoute` / `RouteWaypoint` / `RoutePoint` /
`GpxWaypoint` / `TravelMode` models, `DirectionsInterop`, `wwwroot/js/gmaps.js`, `BrowserInterop`,
`JsLogBridge`, `ActionLog`, `LogEntry`, `KeyRedactor`, and the `LogView` component port into
`RouteTimer.Client`, together with their existing tests. These carry hard-won behaviour — Google's
authentication failures surface only through `console.error` and `window.gm_authFailure`, the
Elevation service must be sampled and interpolated above ten thousand points, and the URL data blob
is logged for cross-check but never trusted — and shall be ported rather than rewritten.

The verbose on-screen action log ports with them. It is the feature's primary diagnostic surface: a
Google Maps key failure is otherwise indistinguishable from a network failure, and the log is what
tells the rider which of the two happened and what origin their key restrictions must allow.

### Changed: elevation becomes mandatory

MapToGarmin treats a failed elevation lookup as a warning and emits a GPX without elevation, because
a navigation course does not need it. RouteTimer's predictor derives gradient, and therefore power
and speed, from elevation; a route without it produces a confidently wrong answer. A failed or
partial elevation lookup shall block submission with an explanatory message rather than degrade.

### Changed: short-link expansion moves into the API

MapToGarmin expands `maps.app.goo.gl` links through a dedicated Caddy route, because a static site
has nowhere else to put a CORS shim. `maps.app.goo.gl` sends no `Access-Control-Allow-Origin` and is
`cross-origin-resource-policy: same-site`, so the browser still cannot fetch it directly, but
RouteTimer's own API can. Moving the shim there keeps it working in local Compose deployments and
behind the shared ingress alike, with no per-deployment ingress edit.

### Changed: the API key is saved

MapToGarmin deliberately never stores the key and scrubs it after every conversion. RouteTimer shall
store it, encrypted, because the rider uses the same key repeatedly across sessions. The honesty of
MapToGarmin's original wording is retained in a different form: the UI shall state plainly that a
saved key is decryptable by the server, is loaded into the page by Google's own script, and can be
spent against by anyone with access to this RouteTimer instance.

## Component Boundaries

### Blazor client

The client owns Google Maps URL parsing, the Maps JavaScript API interop, elevation sampling and
interpolation, GPX generation for submission, the action log, and the two-tab submission panel. It
holds the API key only for the duration of a conversion and scrubs it afterwards, exactly as
MapToGarmin does. It receives the saved key from the API only when the rider starts a conversion.

### RouteTimer API and services

The API owns short-link expansion, encrypted key persistence, prediction GPX generation, and the
Garmin course orchestration. It never receives the Google Maps URL or the intermediate route
geometry; the only route data that reaches it is the finished GPX, through the endpoint that already
accepts GPX uploads.

### Python adapter

The adapter gains one operation: create a Garmin course from GPX bytes. All Garmin-specific request
shapes stay inside it, as they do for activity import.

### PostgreSQL

One new single-row table holds the encrypted Google Maps key. Two new columns on the existing
prediction table record the Garmin course identifier and the time it was pushed.

## Google Maps Route Input

### Submission panel

The Predictions page's "Submit a route" panel shall present two input modes, "Upload GPX" and
"Google Maps route". The existing upload mode is unchanged and remains the default. Mode selection
shall not discard the other mode's entered state within a page session.

The Google Maps mode shall present: an API key field (saved-key status, or a password-type input), a
Google Maps URL field, a travel mode selector, and a Convert-and-predict button. The action log
renders beneath.

### Travel mode

The travel mode selector shall be pre-filled from the URL's own mode when the URL carries one, and
shall default to Bicycling otherwise. RouteTimer predicts cycling; a driving-mode route through a
dual carriageway is a legitimate thing for the rider to ask for but an unlikely default.

### Conversion sequence

1. If the URL is a short link, expand it through `GET /api/routes/short-links/{code}`.
2. Parse the URL. Reject single-point URLs, and routes with more than twenty-five intermediate
   waypoints, with the messages MapToGarmin already uses.
3. Load the Maps JavaScript API with the key, capturing Google's own authentication errors.
4. Request directions for the parsed route and selected travel mode.
5. Request elevation for the returned path, sampling and interpolating above ten thousand points.
6. If elevation is unavailable for any returned point, stop and report that the prediction cannot
   proceed without elevation.
7. Generate the GPX and submit it through the existing `POST /api/predictions` multipart endpoint,
   entering the existing durable job and polling flow indistinguishably from an uploaded file.
8. Scrub the key from client state.

The generated file name shall derive from the route name, as `GpxWriter.SuggestFileName` already
does.

### Short-link expansion endpoint

`GET /api/routes/short-links/{code}` shall:

- accept a code matching `^[A-Za-z0-9_-]{4,64}$` and reject anything else with a `400` problem;
- issue a single non-redirect-following `GET` to a fixed `https://maps.app.goo.gl` upstream;
- send `User-Agent: RouteTimer/1.0` and send no cookie, referer, or authorization header — a
  browser-like user agent causes the endpoint to answer `200` with a JavaScript interstitial and no
  `Location` header, so the non-browser agent is load-bearing;
- return `200` with `{ "resolvedUrl": "..." }` when the upstream answers `301`, `302`, `303`, `307`,
  or `308` with a `Location` header;
- return a `502` problem for any other upstream status or a missing `Location`; and
- return a `504` problem on upstream timeout, with a bounded timeout of ten seconds.

The endpoint shall not follow the returned location, fetch it, or parse it. The client applies its
existing parser to the returned URL and reports failure through the action log with the manual
work-around MapToGarmin already documents: open the short link in a browser tab and paste the
expanded URL.

## Google Maps API Key Persistence

### Secret protection

`AesGcmGarminTokenProtector` shall be generalised into an `AesGcmSecretProtector` that takes its
additional-authenticated-data purpose string as construction input.
`AesGcmGarminTokenProtector` shall remain as a thin wrapper over it, preserving its existing
`RouteTimer:GarminToken:1:1` additional data byte-for-byte so that already-stored Garmin tokens stay
decryptable. The Google Maps key shall use the purpose `RouteTimer:GoogleMapsKey:1:1`.

Key material shall come from a new `GoogleMaps:KeyEncryptionKey` configuration value, a base64
thirty-two byte key, generated by `run.sh` and `run.ps1` on the same path that already generates
`Garmin:TokenEncryptionKey`. When the setting is absent the feature shall degrade rather than fail:
the API reports that key storage is unavailable, and the UI accepts a typed key for the current
conversion but does not offer to save it.

### Storage

A new `GoogleMapsCredentialEntity` shall follow the single-row convention already used by
`GarminConnectionEntity` and `RiderProfileEntity`, with `Id` fixed at `1`. It shall hold the
encryption version, nonce, ciphertext, tag, an updated timestamp, and a non-secret `KeyHint`.

The hint shall be the mask `KeyRedactor.Mask` already produces — first four characters, ellipsis,
last four — so the UI can show which key is saved without decrypting it. Keys shorter than eight
characters mask to an ellipsis alone.

### API surface

| Method and path | Behaviour |
| --- | --- |
| `GET /api/settings/google-maps-key` | Returns `{ "configured": bool, "hint": string?, "storageAvailable": bool }`. Never returns the key. |
| `PUT /api/settings/google-maps-key` | Body `{ "apiKey": "..." }`. Encrypts and stores. Rejects empty or whitespace input, and input longer than 512 characters. `409` when storage is unavailable. |
| `DELETE /api/settings/google-maps-key` | Removes the stored key. `204` whether or not one existed. |
| `POST /api/settings/google-maps-key/use` | Returns `{ "apiKey": "..." }` for the browser to hand to Google's script loader. `404` problem when no key is stored. |

The reveal operation shall be a `POST`, not a `GET`. `UseSameOriginEnforcement` exempts `GET`, `HEAD`,
and `OPTIONS` from its `Sec-Fetch-Site` check, so a `GET` that returns a secret would be readable by
any page served from another port on the same host — precisely the threat that middleware exists to
close. The operation is not idempotent-shaped anyway: it is a deliberate release of a secret.

Beyond the shape of the key, the API shall validate nothing about it. MapToGarmin's stance holds:
any working key is accepted, and no project, product, or restriction requirement is asserted.

### Non-disclosure

The request and response contracts carrying the key shall override `ToString` to redact it, following
`GarminLoginRequest`. The key shall never appear in a URL, query string, log entry, problem detail, or
exception message. The client's action log shall be seeded with `Log.UseRedactionKey` before any
conversion, as MapToGarmin does, so that a Google error message quoting the key is masked on screen
and in a copied log.

### Disclosure to the rider

The key panel shall state that the key is encrypted at rest, that the server can decrypt it, that it
is delivered to the page and to Google when a conversion runs, and that anyone who can sign in to
this RouteTimer instance can spend against it. It shall not claim that the key never leaves the
device, because with server-side storage that would be false.

## Prediction GPX Export

### Source of truth

The exported GPX shall be generated from the persisted prediction segments —
`PredictionSegmentEntity` already carries latitude, longitude, elevation, cumulative distance, and
cumulative moving time — and not from the retained upload. The export therefore reflects the
processed route the prediction was actually computed over, and continues to work after upload
retention lapses.

### Variants

`GET /api/predictions/{id}/gpx` shall accept `timed=false` (default) and `timed=true`.

Both variants shall emit GPX 1.1 with `creator="RouteTimer"`, a `metadata` block carrying the route
name and generation time, and a single `trk`/`trkseg` with `ele` on every point. Coordinates shall be
written to seven decimal places and elevation to one, matching `GpxWriter`, and the document shall be
UTF-8 without a byte order mark.

The `metadata/desc` element shall summarise the prediction in one human-readable line: predicted
moving time, distance, ascent, average speed, average power, confidence, and model version.

The timed variant shall additionally write `time` on every track point, computed as a start instant
plus that point's cumulative predicted moving seconds. The start instant shall be the prediction's
completion time. The untimed variant shall write no track point times, because some course importers
treat a timestamped track as an activity.

### Responses

The endpoint shall return `application/gpx+xml` with
`Content-Disposition: attachment; filename="<slug>.gpx"`, the slug derived from the route name by the
ported `SuggestFileName`. It shall return a `404` problem with code `prediction-not-found` for an
unknown identifier, and a `409` problem with a new `prediction-not-complete` code when the prediction
has no segments because it has not succeeded.

### Client

The prediction detail page shall offer both variants as direct anchors to the endpoint, so the
browser streams the download without interop or in-memory buffering. The prediction history rows
shall offer the untimed variant only, to keep the row uncluttered.

## Garmin Course Push

### Feasibility and risk

Garmin Connect's web interface creates a course from a GPX file in two undocumented steps:
`POST /course-service/course/import`, a multipart upload that returns a parsed skeleton carrying
`geoPoints` but no distance, bounding box, or start point; then `POST /course-service/course`, a JSON
save that returns the stored course with its `courseId`. Both authenticate with the same session the
adapter already holds for activity import.

These endpoints are undocumented and can change without notice. The implementation plan shall
therefore open with a verification spike against a real account. If the spike fails, the remainder of
this specification still ships and the course push is dropped with its findings recorded; the rider's
fallback is the GPX download defined above plus a manual import through Garmin Connect.

The official partner Courses API is explicitly not used. It requires Garmin Connect Developer Program
approval that a personal application does not have, which is the same reasoning already recorded for
activity import.

### Adapter operation

The adapter shall expose `POST /courses`, taking the session token, GPX bytes, a file name, a course
name, an activity type, and optional description and elevation totals. The facade's `TokenSession`
shall gain a `create_course` method that:

1. posts the GPX to `/course-service/course/import` as multipart `application/gpx+xml`;
2. rejects a parsed result with fewer than two geo points;
3. computes each point's cumulative haversine distance, the route's total distance, its bounding box,
   its start point, and the initial bearing from first to last point, because the import step returns
   none of these;
4. builds the create payload with `sourceTypeId` 3 (GPX), `rulePK` 2 (private), a single course line
   carrying the geo points, and WGS84 throughout;
5. posts it to `/course-service/course`; and
6. returns the course identifier, name, distance, and elevation totals.

Where the reference implementations send zero for `elevationGainMeter` and `elevationLossMeter` and
let Garmin backfill from its terrain database, RouteTimer shall send the totals computed from the
prediction's own elevation, which came from Google's Elevation service or the rider's GPX. Garmin may
still override them; the specification asserts what is sent, not what is stored upstream.

Activity type shall map a small stable set of keys to Garmin's type identifiers: `road_biking` (10,
the default), `cycling` (2), `gravel_cycling` (4), and `mountain_biking` (5).

Adapter errors shall translate through the existing `AdapterError` mechanism. A rejection of either
call shall surface as a new `course-rejected` adapter error rather than a generic failure.

### API surface and orchestration

`POST /api/predictions/{id}/garmin-course` shall accept `{ "name": string?, "activityType": string? }`
and return `{ "courseId": long, "courseUrl": string }`.

`GarminCourseService` shall run inside the existing `GarminOperationGate`, so a course push cannot
interleave with an activity import or a session validation. It shall require a connected Garmin
connection and return the existing `garmin-connection-required` or `garmin-reconnect-required`
problems otherwise. It shall decrypt the stored token, call the adapter, and persist the refreshed
token the adapter returns, following the pattern `GarminActivityService` already uses.

The GPX handed to Garmin shall be the untimed course variant, always, regardless of what the rider
last downloaded.

### Recording the result

`PredictionEntity` shall gain nullable `GarminCourseId` and `GarminCourseUploadedAt` columns, exposed
on `PredictionSummaryResponse`. The prediction detail page shall show a "Send to Garmin" action when
the field is empty and a link to the course on Garmin Connect when it is set. A prediction already
pushed shall require an explicit confirmation before being pushed again, so that a double click does
not create a duplicate course.

## Error Handling

New error codes shall be added to `ErrorCodes`: `short-link-code-invalid`, `short-link-unresolved`,
`google-maps-key-not-stored`, `google-maps-key-invalid`, `google-maps-key-storage-unavailable`,
`prediction-not-complete`, and `garmin-course-rejected`. All new endpoints shall return problem
details through `ApiProblems`, as every existing endpoint does.

Client-side Google failures shall surface through the ported action log, which already distinguishes
a rejected key, a referrer restriction, a disabled product, and a billing failure using Google's own
wording.

## Testing

### Ported client tests

MapToGarmin's parser, GPX writer, interpolation, and log redaction tests shall port with their
subjects and shall pass unchanged except for namespace changes.

### New client tests

bUnit tests shall cover: mode switching without state loss; the saved-key status rendering for
stored, absent, and storage-unavailable cases; blocked submission when elevation is unavailable;
successful submission handing a generated GPX to the existing submission path; and key scrubbing
after both success and failure.

### Service and persistence tests

Unit tests shall cover the generalised secret protector against both purposes, including that a
ciphertext written for one purpose fails to decrypt under the other; the credential repository's
single-row upsert and delete; the prediction GPX writer, against golden files for both variants,
including the empty-segment rejection and the timed variant's cumulative time arithmetic; and the
course service's connection-state and gate behaviour with a fake adapter client.

### API tests

Endpoint tests shall cover short-link code validation, upstream redirect and non-redirect handling
with a stubbed handler, the key endpoints' status codes and non-disclosure, the GPX endpoint's
content type, disposition, and `409` path, and the course endpoint's problem mapping.

### Adapter tests

Pytest coverage shall use a fake Garmin client and shall assert the two-call sequence, the computed
distance, bounding box, and bearing, the fewer-than-two-points rejection, the activity type mapping,
and error translation. No test shall contact Garmin or Google.

### Verification

`dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false` and the adapter's pytest
suite shall pass before each commit. The Garmin course push shall additionally be verified once by
hand against a real account, as the spike defines.

## Out of Scope

Per-user scoping of the stored key, or of any existing entity: every persisted entity in this
repository is a single row with `Id = 1` today, and multi-tenant scoping is a repository-wide
migration rather than this feature's concern. Also out of scope: Garmin's official partner Courses
API, course turn points and cue sheets, PacePro strategies, previewing the Google Maps route on a map
before submission, and caching or reusing Google Directions results between conversions.

## Acceptance Criteria

1. A rider can paste a full or short Google Maps URL and an API key, and receive a prediction without
   ever handling a GPX file.
2. A route whose elevation lookup fails does not produce a prediction, and says why.
3. A saved key survives a browser restart, is never returned by the settings read endpoint, and can
   be deleted.
4. With `GoogleMaps:KeyEncryptionKey` unset, conversion still works with a typed key and the UI does
   not offer to save it.
5. Existing Garmin tokens stored before this change remain decryptable.
6. A completed prediction downloads as a GPX in both variants, and Garmin Connect accepts the untimed
   variant on manual import.
7. A completed prediction pushes to Garmin Connect as a course, the prediction records the course
   identifier, and the detail page links to it.
8. Uploading a GPX to the Predictions page behaves exactly as it did before this change.

# Garmin Activity Import Design

**Date:** 2026-08-25
**Status:** Approved in conversational design review; awaiting review of this written specification

## Purpose

RouteTimer shall let its authenticated rider connect a Garmin Connect account, browse road-cycling and gravel activities, select up to ten activities, and import their original FIT files as training evidence. Imported files shall use the existing retained-upload, parsing, cleaning, model-build, and projected-speed workflow.

This feature supplements `docs/superpowers/specs/2026-08-24-route-timer-design.md` and `docs/superpowers/specs/2026-08-25-route-timer-step-9-api-ui-design.md`. Those specifications remain authoritative where this document does not explicitly extend them.

## Confirmed Scope

The feature shall:

- use an unofficial personal-use Garmin Connect integration;
- use the actively maintained, MIT-licensed `python-garminconnect` library, pinned to version `0.3.4`;
- keep the Garmin-specific implementation behind a private Python adapter;
- support Garmin username/password login and MFA from the RouteTimer UI;
- keep credentials and MFA codes only in adapter memory for the duration of login;
- persist only an encrypted Garmin token bundle and non-secret connection metadata;
- list Garmin road-cycling and gravel activities newest first;
- exclude indoor cycling, e-bike, mountain-bike, and all non-cycling activities;
- paginate the Garmin list in pages of 50;
- let the rider select at most ten activities per import;
- report an outcome for every selected activity;
- retain downloaded original FIT files and feed them into the existing training pipeline;
- make repeated imports idempotent by Garmin activity ID and FIT content hash;
- show whether a Garmin activity has already been imported; and
- allow the rider to disconnect Garmin without deleting imported training evidence.

The feature shall not:

- use Garmin's official business-only Activity API;
- expose Garmin tokens to Blazor;
- store Garmin passwords or MFA codes;
- automatically synchronize future Garmin activities;
- import indoor, e-bike, mountain-bike, running, swimming, or other activity types;
- create a second FIT parser, training cleaner, model builder, or prediction path; or
- contact Garmin from automated tests or CI.

## Integration Choice

The preferred integration is a small Python HTTP adapter built on `python-garminconnect==0.3.4`. A native C# client would require RouteTimer to maintain undocumented Garmin SSO, MFA, DI OAuth token rotation, rate limiting, and download behavior. A Playwright implementation would add Chromium, slower requests, and short-lived cookie sessions. The adapter keeps these unstable concerns replaceable without leaking Garmin response types into RouteTimer's domain or UI.

The pinned library is an implementation dependency, not a domain dependency. All use of it stays inside the adapter. The adapter must not use the library's filesystem token store; RouteTimer owns encrypted token persistence in PostgreSQL.

## Component Boundaries

### Blazor client

The Blazor client calls only the authenticated RouteTimer API. It owns connection forms, MFA entry, activity selection, pagination, import outcomes, and disconnect confirmation. It never receives a Garmin access token, refresh token, token bundle, cookie, or raw Garmin response.

### RouteTimer API and services

The .NET application remains the public boundary. It:

- enforces the existing authenticated `rider` policy;
- validates public request limits;
- maps public contracts to focused Garmin application services;
- decrypts and encrypts the token bundle;
- stores connection and import-link data;
- invokes the private adapter;
- filters activity types defensively even when the adapter already filtered them; and
- passes downloaded FIT streams to the existing `TrainingUploadService`.

The .NET code depends on an `IGarminAdapterClient` interface. Tests replace that interface with deterministic fakes.

### Python adapter

The adapter is an internal-only HTTP service with no published host port. It exposes a versioned contract for:

- starting login;
- completing MFA;
- validating or refreshing a saved token bundle;
- listing an activity page; and
- downloading one original FIT file.

The adapter translates library and Garmin failures into a small stable internal error set. It does not access PostgreSQL, the RouteTimer encryption key, Keycloak tokens, stored uploads, training activities, jobs, models, or predictions.

### PostgreSQL

PostgreSQL stores one single-rider Garmin connection and zero or more links between Garmin activity IDs and retained FIT uploads. Existing stored-upload and training tables remain the source of truth for downloaded evidence and parsed activities.

## Authentication and MFA

### Public flow

1. The Training page requests Garmin connection status.
2. When disconnected, the rider submits Garmin email and password to the authenticated RouteTimer API over HTTPS.
3. RouteTimer forwards those credentials directly to the adapter and does not persist them.
4. If Garmin completes login without MFA, the adapter returns connection identity metadata plus a token bundle.
5. If Garmin requires MFA, the adapter returns an opaque challenge ID. The UI changes to an MFA form.
6. The rider submits the challenge ID and MFA code to RouteTimer, which forwards both to the adapter.
7. On successful MFA, the adapter returns connection identity metadata plus a token bundle.
8. RouteTimer encrypts and stores the token bundle, then returns a token-free connection response to Blazor.

The adapter retains an in-progress MFA session, including the submitted Garmin credentials and transient HTTP state, in memory for at most five minutes. Completing, cancelling, expiring, or failing the challenge clears that state. Adapter restart invalidates active challenges and requires the rider to restart login. This is acceptable for the current single-rider, single-adapter deployment.

### Credential handling

Garmin email, password, MFA code, raw tokens, cookies, and token bundles must be excluded from:

- structured logs and scopes;
- request-body logging;
- traces and metrics labels;
- exception messages;
- problem responses;
- database columns other than the encrypted token fields; and
- adapter filesystem writes.

Request and response DTO `ToString` output must not reveal secret fields. Login endpoints must not bind secret values into route values or query strings.

### Token persistence

The `garmin_connections` table contains exactly one row with ID `1` when a connection has been established. It stores:

- connection state (`connected` or `reconnect-required`);
- optional Garmin user ID;
- optional safe display name;
- encryption format version;
- AES-GCM nonce;
- AES-GCM ciphertext;
- AES-GCM authentication tag;
- last successful validation time; and
- updated time.

The plaintext is the complete token bundle required by the pinned library, serialized as UTF-8 JSON. RouteTimer encrypts it with AES-256-GCM. The 32-byte key is supplied as a base64 deployment secret through `Garmin__TokenEncryptionKey`; startup fails closed when the adapter is enabled and the key is missing or invalid. The encryption key is never stored in PostgreSQL or source control. Authenticated additional data binds ciphertext to the RouteTimer Garmin-token purpose, connection row ID, and encryption format version.

Every successful adapter call returns the current token bundle. RouteTimer persists it when it differs from the decrypted input so refresh-token rotation is not lost. Token-using operations are serialized for the single connection to prevent concurrent refreshes from overwriting a newer bundle.

A deterministic authentication failure during validation or refresh sets the connection state to `reconnect-required`. Transient adapter, Garmin, rate-limit, or network failures retain the encrypted bundle and leave the established connection recoverable. Successful reauthentication replaces the previous encrypted bundle and returns the state to `connected`.

Disconnect deletes the connection row and invalidates any active adapter login challenge. It does not delete `garmin_activity_imports`, stored FIT uploads, parsed activities, model versions, or predictions.

## Garmin Activity Model and Filtering

The adapter maps Garmin data into its own stable activity summary contract. RouteTimer public contracts never expose the library's dictionaries or Garmin's raw JSON.

Each visible activity contains:

- Garmin activity ID as an opaque non-empty string;
- activity name;
- UTC start time;
- canonical type (`road-cycling` or `gravel-cycling`);
- optional distance in metres;
- optional duration in seconds;
- optional ascent in metres;
- optional average power in watts; and
- whether RouteTimer has already linked it to a retained upload.

The adapter maps Garmin `road_biking` to `road-cycling` and `gravel_cycling` to `gravel-cycling`. All other keys are excluded. RouteTimer applies the same canonical allow-list before returning data or accepting an import ID from a page result. A selected activity ID must belong to a road/gravel activity returned by Garmin at import time; clients cannot use the import endpoint as an arbitrary Garmin download proxy.

Missing optional summary metrics do not hide an activity. FIT parsing and training eligibility remain responsible for deciding whether the downloaded evidence has the required timestamped GPS, elevation, speed, and power records.

## Persistence and Idempotency

The new `garmin_activity_imports` table stores:

- Garmin activity ID as its primary key;
- retained upload ID as a required foreign key;
- linked time; and
- the safe activity name captured at import time for diagnostics.

The retained upload remains the source of original FIT bytes and SHA-256 identity. The link is the source of Garmin ID idempotency and the `alreadyImported` projection.

Import acceptance is transactional:

1. If the Garmin activity ID already has a link, return `already-imported` without downloading it again.
2. Otherwise download the FIT file and calculate its SHA-256 hash through the existing bounded upload service.
3. If the hash is new, create the retained upload, parse job, and Garmin link in one transaction.
4. If an existing retained FIT upload has the same hash, create the Garmin link to that upload and return `duplicate` without creating a second parse job.
5. Concurrent imports of the same Garmin ID converge on the unique link and return one accepted result plus idempotent duplicate results.

Deleting a training activity continues to delete its retained upload. The Garmin link cascades with that upload, so the activity becomes importable again after deletion. Disconnecting Garmin leaves links and evidence intact.

## API Surface

All endpoints require the existing `rider` role.

- `GET /api/garmin/connection` returns connection state and safe identity metadata.
- `POST /api/garmin/connection/login` accepts email and password and returns `connected` or `mfa-required` with an opaque challenge ID.
- `POST /api/garmin/connection/mfa` accepts challenge ID and MFA code and returns `connected`.
- `DELETE /api/garmin/connection` removes the saved connection and returns `204 No Content`; deleting an absent connection is also `204`.
- `GET /api/garmin/activities?cursor={opaque}` returns up to 50 activities and an optional next cursor.
- `POST /api/garmin/activities/import` accepts one to ten distinct Garmin activity IDs and returns one ordered result per submitted ID.

The cursor is an opaque RouteTimer value representing the adapter offset. Clients must not construct or alter it. Invalid cursors return `400 Bad Request` with `garmin-cursor-invalid`.

Import results contain Garmin activity ID, safe activity name when available, outcome, optional upload ID, optional parse job ID, and optional stable error code. Outcomes are:

- `accepted`;
- `already-imported`;
- `duplicate`;
- `invalid-fit`; and
- `download-failed`.

An accepted result contains upload and parse job IDs. `Already-imported` and `duplicate` results contain the linked upload ID and that upload's original parse job ID. Invalid or failed results contain neither. Mixed outcomes return `202 Accepted`; one per-item failure does not turn the batch into a request-level problem.

## Import Orchestration

RouteTimer validates the requested count and distinct IDs before contacting the adapter. It then processes selections sequentially to limit Garmin request pressure and memory use. Cancellation stops work not yet started but does not roll back accepted earlier selections.

For each selection, RouteTimer:

1. checks the existing Garmin link;
2. asks the adapter to re-read the activity summary and verify its allowed type;
3. streams the original FIT file through the existing 50 MiB bound;
4. passes the stream, a deterministic safe filename, and Garmin provenance to the training upload service;
5. records the link transactionally with retained-upload acceptance; and
6. returns the per-activity outcome.

The safe filename is `<sanitized-activity-name>-<garmin-activity-id>.fit`, truncated to the existing 512-character persistence limit. Sanitization removes path separators and control characters and falls back to `garmin-<activity-id>.fit`.

Accepted FIT files follow the existing pipeline:

`stored upload -> ParseTraining job -> FIT parser -> training cleaner -> training activity -> coalesced BuildModel job -> current rider model -> projected speed`

No Garmin-specific logic appears after stored-upload acceptance.

## Error Handling

Public Garmin problems use RFC problem details and stable codes:

- `garmin-credentials-rejected` for invalid Garmin credentials;
- `garmin-mfa-invalid` for an invalid MFA code;
- `garmin-challenge-expired` for an absent, expired, or restarted challenge;
- `garmin-connection-required` when listing or importing without a usable connection;
- `garmin-reconnect-required` when saved tokens can no longer refresh;
- `garmin-rate-limited` when Garmin rejects request frequency;
- `garmin-unavailable` for transient Garmin failures;
- `garmin-adapter-unavailable` when the internal adapter cannot be reached;
- `garmin-response-invalid` when Garmin returns an unusable shape;
- `garmin-cursor-invalid` for malformed pagination state; and
- `garmin-import-limit` when a request contains zero, more than ten, or duplicate IDs.

Garmin authentication failures do not use an HTTP `401` response because that status is reserved for RouteTimer/Keycloak authentication. Invalid credentials and MFA use `400`; missing or expired connection state uses `409`; rate limiting uses `429`; adapter or Garmin availability failures use `503`.

Unexpected adapter details, Garmin bodies, stack traces, SQL, tokens, credentials, and internal URLs never appear in public responses.

## Training Page Experience

The existing manual FIT upload remains available. A Garmin section appears above it.

When disconnected, the section shows:

- Garmin email and password inputs;
- a statement that credentials are used only to establish the connection and are not saved;
- a connect action; and
- safe inline failures.

When MFA is required, the credential form is replaced by an MFA form tied to the opaque challenge. Expiration returns the user to the credential form.

When connected, the section shows:

- safe Garmin identity metadata;
- a disconnect action with confirmation;
- a newest-first list of road and gravel activities;
- date, name, type, distance, duration, ascent, and average power where available;
- an already-imported state that cannot be selected;
- checkboxes for importable rows;
- selected count and a maximum of ten;
- an Import selected action;
- Load more when a next cursor exists; and
- per-activity import outcomes and parse-job progress.

Connection, activity-list, import, and existing RouteTimer training states fail independently. A Garmin failure must not hide retained training activities, model status, manual upload, or deletion controls.

The UI explicitly distinguishes download accepted, already imported, duplicate FIT, invalid FIT, Garmin download failure, and later parse-job failure. Activity loading has loading, empty, failure, retry, and pagination states. Duplicate submissions are disabled while login, MFA, list, import, or disconnect operations are active.

## Adapter Contract and Deployment

The adapter lives in a focused top-level directory with its own locked Python dependencies, tests, Dockerfile, and health endpoint. It uses a production ASGI server and binds only inside the Compose network.

Docker Compose adds the adapter service with:

- no host port;
- a health check;
- a read-only application filesystem where practical;
- no database credentials;
- no token-encryption key;
- no Garmin credentials or token volume; and
- outbound HTTPS access to Garmin.

The RouteTimer API receives the adapter base URL and token-encryption key through configuration. Production startup validates both. The existing public Caddy routing does not expose the adapter.

The unofficial integration may break when Garmin changes private APIs. The adapter boundary and pinned dependency make that an isolated maintenance event. Dependency upgrades require adapter contract tests plus the opt-in real-account smoke test before release.

## Testing

### Python adapter

Unit and contract tests use a fake Garmin client and cover:

- successful login without MFA;
- MFA challenge creation, completion, expiration, and cleanup;
- credential and token redaction;
- token validation and refresh rotation;
- road/gravel mapping and exclusion of every other fixture type;
- page size and continuation behavior;
- FIT streaming and size/error propagation;
- malformed library/Garmin responses; and
- stable internal error translation.

No automated adapter test contacts Garmin.

### .NET services and persistence

Service tests use a fake `IGarminAdapterClient` and cover:

- credential disposal after login;
- AES-GCM encryption, decryption, authentication failure, and key validation;
- token rotation persistence;
- deterministic versus transient connection failure state;
- defensive road/gravel filtering;
- cursor validation and pagination;
- one-to-ten selection validation;
- sequential partial-success imports;
- Garmin-ID and hash idempotency;
- filename sanitization and size limits; and
- reuse of the existing training acceptance pipeline.

Real PostgreSQL tests cover migration, encrypted connection round trip, Garmin-link round trip, cascade behavior, unique Garmin ID enforcement, hash-duplicate linking, and concurrent same-ID imports. EF pending-model verification must report no changes after the migration.

### API and client

API tests cover rider authorization, safe problem mappings, absence of secrets in responses, login/MFA lifecycle, status, disconnect idempotency, pagination contracts, import contracts, and cancellation.

bUnit tests cover login, MFA, challenge expiry, connected identity, list loading/empty/failure states, activity metadata, allowed selection, imported-row disabling, the ten-item limit, load more, per-activity outcomes, job polling, retry, and disconnect confirmation. Existing tests remain authoritative for manual upload, training parsing, model rebuild, and model status.

### Verification

Verification shall run:

- Python formatting, linting, type checking, unit tests, and package build;
- the full .NET test suite and release build;
- PostgreSQL integration tests and EF pending-model checks;
- Docker builds for RouteTimer and the adapter;
- Docker Compose configuration validation; and
- an opt-in documented real-Garmin smoke test outside CI.

The real-account smoke test covers login, MFA when enabled, saved-token reuse, activity pagination, road/gravel filtering, one FIT import, disconnect, and reconnect. Test credentials and tokens must not be copied into logs, fixtures, CI secrets, or repository files.

## Baseline Test Condition

The worktree was created from `main` commit `05937f9`. Before this specification was written, `dotnet test RouteTimer.slnx` produced these results:

- RouteTimer.Domain.Tests: 15 passed;
- RouteTimer.Services.Tests: 270 passed;
- RouteTimer.Api.Tests: 66 passed;
- RouteTimer.Persistence.Tests: 143 passed;
- RouteTimer.EndToEnd.Tests: no tests discovered; and
- RouteTimer.Client.Tests: produced no result for more than three minutes and the baseline run was cancelled.

The rider explicitly approved proceeding while recording the client-test hang as a pre-existing baseline issue. Feature verification must run the client tests with bounded hang diagnostics and report the final condition rather than treating the interrupted baseline as a pass.

## Acceptance Criteria

1. An authenticated rider can connect Garmin with credentials and complete MFA without RouteTimer persisting the password or MFA code.
2. Only AES-256-GCM-encrypted Garmin token data and safe identity metadata are stored in PostgreSQL.
3. Saved tokens are reused and rotated tokens are persisted without exposing them to Blazor.
4. The Training page lists only road-cycling and gravel activities, 50 at a time, newest first.
5. Indoor, e-bike, mountain-bike, and non-cycling activities cannot be listed or imported through the public API.
6. The rider can select one to ten non-imported activities and receives one outcome for every selection.
7. Accepted FIT files use the existing upload, parse, clean, model-rebuild, and projected-speed path.
8. Re-importing by Garmin ID or identical FIT hash creates no duplicate evidence or parse job.
9. Partial Garmin failures do not undo successful selections or hide existing RouteTimer data.
10. Disconnect removes the encrypted connection but preserves imported training evidence and its model history.
11. Automated tests use fakes and never contact Garmin; the real-account check is explicit and opt-in.
12. The Python adapter is internal-only, has no host port or database access, and can be replaced without changing public RouteTimer contracts.

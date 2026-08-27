# Open in PaceTracker Design

**Date:** 2026-08-27

## 1. Purpose

RouteTimer will add an **Open in PaceTracker** action to a completed prediction. The action hands the prediction's timed GPX to the RoutePacer Blazor WebAssembly PWA at `https://pacetracking.tqaentry.com` without exposing RouteTimer authentication to RoutePacer.

The handoff must implement RoutePacer Contract v1 exactly. It is a coordinated feature across two independently deployed applications, so RouteTimer keeps the button hidden until its signer, public payload origin, and the matching RoutePacer intake are enabled.

## 2. Scope

This design covers the RouteTimer side of the handoff:

- producing the timed GPX for a completed prediction;
- creating a short-lived, single-use payload grant;
- signing the RoutePacer invocation with RouteTimer's ECDSA private key;
- allowing RoutePacer to fetch the granted payload across origins;
- opening RoutePacer from the prediction detail page without being blocked as a popup;
- cross-repository contract fixtures, automated tests, configuration, and rollout documentation.

RoutePacer parsing, signature verification, GPX import, IndexedDB persistence, and tracking remain owned by the RoutePacer plan. Web Share Target support, key rotation automation, arbitrary target hosts, and payload formats other than timed GPX are outside this feature.

## 3. Existing Context

`PredictionDetail.razor` already shows GPX downloads and Garmin actions when stored prediction segments exist. `PredictionQueryService.GetGpxSourceAsync` and `PredictionGpxWriter.Write(source, timed: true)` already produce the handoff content. RouteTimer's API applies an authenticated rider fallback policy to every endpoint unless an endpoint explicitly opts out.

RouteTimer and its PostgreSQL database deploy together behind a shared Caddy ingress. The Blazor client and API share an origin, but RoutePacer is a different origin. RouteTimer currently has no CORS policy because its existing browser API calls are same-origin.

The authoritative RoutePacer intake contract is defined by Task 8 of `../RoutePacer/docs/superpowers/plans/2026-08-27-offline-first-route-pacer.md`.

## 4. Contract v1

The RoutePacer invocation URL has these query parameters exactly once:

```text
https://pacetracking.tqaentry.com/open
  ?src=rt
  &v=1
  &payload=<absolute-https-payload-url>
  &name=<route-name>
  &ts=<unix-milliseconds>
  &sig=<base64url-signature>
```

RouteTimer signs this UTF-8 byte sequence, with a single line feed between fields and no trailing line feed:

```text
rt\n1\n{payload-absolute-uri}\n{name-or-empty}\n{unix-milliseconds}
```

The signature algorithm is ECDSA P-256 with SHA-256. Signature bytes use the fixed-width IEEE-P1363 `r || s` format and are encoded as unpadded base64url. RouteTimer owns the private key. RoutePacer receives only the public JWK.

Canonicalization occurs before query-string encoding:

- `payload-absolute-uri` is the absolute URI string produced by RouteTimer, including the opaque token path segment;
- `name-or-empty` is the unescaped .NET string, or the empty string when no name is available;
- the timestamp uses invariant ASCII decimal digits;
- RouteTimer signs the unescaped canonical values, then percent-encodes each query value once;
- RoutePacer parses and percent-decodes each value once before reconstructing the same canonical sequence.

Both repositories contain the same fixed valid and tampered fixtures. The fixture contains the public JWK, canonical UTF-8 text, payload URL, route name, timestamp, P1363 signature, full invocation URL, and a version identifier. Test-only private key material may live under the test tree but never under a published `wwwroot` or production configuration path.

## 5. Considered Approaches

### 5.1 PostgreSQL payload grants and asymmetric signing — selected

RouteTimer writes the exact timed GPX bytes to a short-lived PostgreSQL row and signs an invocation containing a random bearer URL. RoutePacer verifies with a public key and atomically consumes the row.

This works across restarts and replicas, matches the existing durable deployment model, keeps authentication boundaries separate, and implements RoutePacer Contract v1 exactly. The cost is one small table, repository, and migration.

### 5.2 In-memory payload cache — rejected

An `IMemoryCache` implementation is simpler, but loses outstanding handoffs on restart, routes requests to the wrong replica when more than one API instance exists, and needs extra synchronization to make consumption atomic. These are production correctness failures rather than optional hardening.

### 5.3 Shared HMAC secret — rejected

A shared secret embedded in a Blazor WebAssembly application is public to every browser user. It would allow clients to forge RouteTimer invocations and contradicts the RoutePacer contract. ECDSA lets RoutePacer verify without possessing signing authority.

### 5.4 Direct anonymous prediction download — rejected

Making the existing timed-GPX endpoint anonymous would expose stable prediction identifiers and blur its current rider authorization boundary. A scoped, random, expiring grant keeps the existing download endpoint authenticated and makes anonymous access single-purpose.

## 6. Architecture and Boundaries

### 6.1 Contract and signing

`RoutePacerInvocationCanonicalizer` owns the exact Contract v1 byte sequence. `IRoutePacerInvocationSigner` signs those bytes. The production signer imports one configured P-256 private key at startup and uses `DSASignatureFormat.IeeeP1363FixedFieldConcatenation` explicitly.

Canonicalization is a pure service with fixture-driven tests. URL construction is separate from signing so query ordering and percent encoding can be tested independently.

### 6.2 Payload store

`IRoutePacerPayloadStore` exposes two operations:

```csharp
Task<RoutePacerPayloadGrant> StoreAsync(
    byte[] content,
    DateTimeOffset expiresAt,
    CancellationToken cancellationToken);

Task<byte[]?> ConsumeAsync(
    string token,
    DateTimeOffset now,
    CancellationToken cancellationToken);
```

`RoutePacerPayloadGrant` returns the raw token only to the caller that creates the invocation. The database stores only `SHA-256(token)` and never the bearer token itself.

The `route_pacer_payloads` table contains:

- `Id` — UUID primary key used only internally;
- `TokenHash` — 32-byte unique SHA-256 digest;
- `Content` — timed GPX bytes in `bytea`;
- `CreatedAt` and `ExpiresAt` — UTC timestamps;
- `ConsumedAt` — nullable UTC timestamp.

Tokens contain 32 cryptographically random bytes encoded as unpadded base64url. The route constraint accepts exactly 43 base64url characters before any database lookup.

Consumption is one atomic PostgreSQL statement that sets `ConsumedAt` and returns `Content` only when the hash matches, `ConsumedAt IS NULL`, and `ExpiresAt > now`. Concurrent consumers therefore produce exactly one success. Missing, malformed, expired, and consumed tokens are indistinguishable to the caller.

Expired rows and consumed rows older than the ten-minute payload lifetime are deleted opportunistically before a new grant is inserted. A supporting expiry index keeps cleanup bounded. A separate background worker is unnecessary for the expected one-rider volume.

### 6.3 Invocation orchestration

`RoutePacerLinkService.CreateAsync(Guid predictionId, CancellationToken)`:

1. refuses the operation when the feature is disabled;
2. reads the prediction GPX source through `PredictionQueryService`;
3. returns the existing prediction-not-found or prediction-not-complete semantics when applicable;
4. writes the exact timed GPX with `PredictionGpxWriter`;
5. stores it for ten minutes using the injected `TimeProvider`;
6. constructs an absolute HTTPS payload URL from the configured RouteTimer public base URL and raw token;
7. canonicalizes and signs all Contract v1 fields;
8. returns the complete RoutePacer `/open` URL.

If signing or persistence fails, no invocation URL is returned. A successfully stored payload for which later signing fails is harmless and becomes eligible for expiry cleanup.

### 6.4 API endpoints

RouteTimer adds a focused `RoutePacerEndpoints` endpoint class rather than expanding `PredictionEndpoints` with an unrelated anonymous trust boundary.

Authenticated endpoints:

- `GET /api/routepacer/status` returns `{ "enabled": true|false, "routePacerOrigin": "https://pacetracking.tqaentry.com" }`. The normal rider fallback policy applies. The origin is non-secret and gives the client an independently configured allowlist for navigation.
- `POST /api/predictions/{id:guid}/routepacer-link` creates a grant and returns `{ "url": "..." }`. The normal rider fallback policy and same-origin mutation enforcement apply.

The link endpoint returns existing stable problem codes:

- `404 prediction-not-found` when the prediction does not exist;
- `409 prediction-not-complete` when timed GPX cannot be produced;
- `503 routepacer-handoff-disabled` when configuration has disabled the integration.

Anonymous endpoint:

- `GET /api/routepacer/payloads/{token}` explicitly calls `.AllowAnonymous()` and applies only the named RoutePacer payload CORS policy.

On success the payload endpoint returns the raw bytes as `application/gpx+xml` with `Cache-Control: no-store`, `Pragma: no-cache`, and `X-Content-Type-Options: nosniff`. On every invalid, expired, or consumed grant it returns the same `404` response. It never redirects and does not expose prediction metadata.

The CORS policy allows only the configured RoutePacer origin, only `GET` and `HEAD`, no credentials, and no wildcard origin. `UseCors` runs after routing and before authentication/authorization; the existing same-origin middleware already permits `GET`, `HEAD`, and `OPTIONS`.

Application logs contain only a stable result code and, for invocation creation, the prediction ID already used by authenticated RouteTimer operations. They never contain bearer tokens, invocation URLs, signatures, query strings, or GPX bytes. Deployment documentation requires access-log suppression or URI redaction for `/api/routepacer/payloads/*` at the shared ingress.

### 6.5 Client flow

The prediction detail page requests RoutePacer status only after it has loaded a prediction with stored segments. It renders the action only when the server reports that the integration is enabled. A status-request failure hides the action and records no page-level failure because GPX download and Garmin actions remain usable.

The click handler must preserve the browser's user activation:

1. synchronously call a small JavaScript-module function that opens a same-origin blank placeholder tab and returns a retained handle identifier;
2. if the browser returns no window, show explicit popup-blocked guidance and do not create a payload grant;
3. await the authenticated `CreateRoutePacerLinkAsync` API call;
4. navigate the retained window to the returned, allowlisted RoutePacer URL;
5. close the placeholder on API failure or page cancellation.

The JavaScript module refuses to navigate the handle unless the target uses HTTPS and its origin equals the `routePacerOrigin` obtained independently from the status endpoint and passed by .NET. The new interop follows the existing collocated ES-module pattern used by RouteTimer rather than adding a global object and script tag.

The button is disabled while a handoff is being created and reports API failures through `ProblemMessage`. Repeated clicks while the operation is active do nothing. Disposal closes any still-blank placeholder and cancels the request.

## 7. Configuration and Key Management

The API binds `RoutePacerHandoffOptions` from `RoutePacerHandoff`:

```json
{
  "RoutePacerHandoff": {
    "Enabled": false,
    "RoutePacerBaseUrl": "https://pacetracking.tqaentry.com",
    "RouteTimerPublicBaseUrl": "https://routetimer.tqaentry.com",
    "PayloadLifetimeMinutes": 10,
    "SigningPrivateKeyPem": ""
  }
}
```

Tracked configuration keeps `Enabled` false and the private key empty. Production supplies the PEM through secret-file or environment-backed configuration; it is never committed, returned by an endpoint, written to logs, or copied into the Blazor client.

Startup validation applies these rules:

- `PayloadLifetimeMinutes` is exactly `10` for Contract v1;
- both base URLs are absolute HTTPS origins with no query or fragment;
- `RoutePacerBaseUrl` has no path other than `/`;
- enabling requires a non-empty importable ECDSA P-256 private key;
- disabled configuration may omit the key so tests and local development start safely.

The corresponding public JWK is exported during coordinated deployment and installed in RoutePacer configuration before either side is enabled.

## 8. Error and Abuse Handling

The random 256-bit token is the only credential accepted by the anonymous endpoint. The signed RoutePacer invocation prevents an attacker from replacing the payload URL, route name, source, version, or timestamp. RoutePacer independently rejects invocations older than ten minutes, more than sixty seconds in the future, non-HTTPS payload URLs, and non-allowlisted payload hosts.

Payload size is capped at 52,428,800 bytes at the store boundary even though generated prediction GPX files are expected to be much smaller. The payload endpoint provides `Content-Length`, allowing RoutePacer to reject oversized content before reading it, while RoutePacer retains its counting-stream limit for defensive verification.

One-time consumption means a successful fetch cannot be retried. RoutePacer owns user-facing recovery after consumption; RouteTimer deliberately returns no distinction between expired, consumed, and nonexistent grants.

## 9. Testing Strategy

### Contract tests

- RouteTimer canonicalization and ECDSA verification reproduce the fixed valid fixture byte-for-byte.
- Mutation of every signed field fails verification against the fixture signature.
- RoutePacer consumes the same fixture independently.
- A repository check proves no symmetric or private signing material exists under RoutePacer `wwwroot`.

### Service and persistence tests

- A timed GPX is stored with the configured ten-minute expiry.
- A 32-byte random token is returned while only its SHA-256 digest is persisted.
- First consumption returns the original bytes; second consumption returns null.
- Two concurrent consumers produce one success and one null result.
- Expired, malformed, and unknown tokens return null.
- Opportunistic cleanup removes expired and sufficiently old consumed rows.
- Missing and incomplete predictions preserve the existing public error semantics.
- The injected `TimeProvider` controls the timestamp and expiry.

### API tests

- Status and link creation require an authenticated rider.
- Link creation returns a valid Contract v1 URL when enabled.
- Disabled configuration returns status false and rejects link creation.
- Payload retrieval is anonymous, single-use, no-store, and has the exact GPX media type.
- The allowed RoutePacer origin receives the CORS header; an unrelated origin does not.
- Anonymous access to existing prediction and timed-GPX endpoints remains unauthorized.
- No endpoint response exposes the private key or token hash.

### Client and browser tests

- bUnit tests cover hidden/visible state, loading state, duplicate-click suppression, API problems, popup-blocked guidance, cancellation, and placeholder cleanup.
- JavaScript unit tests cover handle creation, exact-origin navigation, refusal of HTTP or foreign origins, and close behavior.
- Playwright proves a real click opens one tab, survives the awaited API call, reaches the RoutePacer `/open` URL, and does not leave a blank tab after failure.
- A production-like cross-origin test proves RoutePacer can fetch the payload and that a second fetch returns 404.

## 10. Rollout and Operations

Deployment order is deliberately one-way:

1. deploy RoutePacer Contract v1 intake with `Enabled=false`;
2. generate RouteTimer's production P-256 key outside both repositories;
3. configure RouteTimer's private key and configure RoutePacer with the public JWK and allowed RouteTimer host;
4. deploy RouteTimer with handoff disabled and run the shared valid/tampered fixtures against production-like origins;
5. enable RoutePacer intake;
6. enable RouteTimer handoff, which makes the button visible;
7. smoke-test creation, cross-origin retrieval, one-time consumption, expiry, and popup handling;
8. monitor aggregate status codes only, excluding payload paths and invocation query strings from logs.

Rollback disables the RouteTimer feature first, hiding the button and preventing new grants, then disables RoutePacer intake. Existing grants expire within ten minutes and are removed opportunistically. Key replacement is a coordinated deployment outside Contract v1; automatic multi-key rotation is not added by this feature.

## 11. Acceptance Criteria

1. A rider can open a completed prediction in PaceTracker from Chrome or Edge without a popup-blocker failure.
2. RoutePacer receives the exact timed GPX and imports it through Contract v1.
3. RoutePacer verifies a P-256 signature without possessing RouteTimer's private key.
4. Payload URLs are absolute HTTPS URLs on the configured RouteTimer host, expire after ten minutes, and succeed at most once even under concurrent requests.
5. Only the payload endpoint is anonymous; existing prediction resources remain rider-protected.
6. Cross-origin retrieval succeeds only for the configured RoutePacer origin and never uses credentials.
7. Outstanding grants survive an API restart and work across API replicas sharing PostgreSQL.
8. The feature is hidden while disabled, and coordinated enable/rollback steps are documented.
9. Tokens, signatures, invocation URLs, private keys, and GPX bytes are absent from application and ingress logs.
10. RouteTimer unit, persistence, API, client, JavaScript, and browser tests pass, and both repositories pass the shared valid/tampered contract fixtures.

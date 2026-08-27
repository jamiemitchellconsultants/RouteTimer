# Open in PaceTracker Design

**Date:** 2026-08-27

## 1. Purpose

RouteTimer will add an **Open in PaceTracker** action to a completed prediction. RouteTimer usually runs privately in Docker on the rider's computer, while the RoutePacer Blazor WebAssembly PWA runs on the rider's phone from `https://pacetracking.tqaentry.com`. The handoff must therefore cross both a network boundary and a device boundary without making RouteTimer publicly reachable.

The selected design uploads the timed GPX from the local RouteTimer API to a short-lived public relay owned by the RoutePacer deployment. RouteTimer signs the relay payload URL using RoutePacer Contract v1 and shows the resulting RoutePacer deep link as a QR code. The rider scans the QR code on the phone, RoutePacer verifies the signature, fetches the GPX from its same-origin relay, imports it, and consumes the relay object once.

## 2. Scope

This design covers the RouteTimer side of the handoff:

- producing the timed GPX for a completed prediction;
- uploading the GPX from the private RouteTimer container to the public relay over outbound HTTPS;
- signing the returned public payload URL with RouteTimer's ECDSA private key;
- presenting a scannable QR code, copyable link, expiry, and recovery states;
- configuration, secret handling, contract fixtures, automated tests, and coordinated rollout;
- defining the exact relay and intake requirements that must be implemented separately in the RoutePacer repository.

The RouteTimer implementation plan must not prescribe RoutePacer file edits. It instead produces a standalone, copy-paste prompt for use in the RoutePacer repository.

RoutePacer route parsing, IndexedDB persistence, tracking, and offline behavior remain owned by RoutePacer. Web Share Target support, tunnels into the private RouteTimer host, arbitrary relay providers, encrypted relay payloads, automatic key rotation, and payload formats other than timed GPX are outside this feature.

## 3. Deployment Topology

The expected topology is:

```text
Computer                                             Public host / phone

RouteTimer browser
    | same-origin authenticated POST
    v
RouteTimer API in private Docker container
    | outbound HTTPS + relay bearer credential
    v
https://pacetracking.tqaentry.com/api/handoffs
    | stores plaintext GPX for at most 10 minutes
    v
RouteTimer browser displays signed /open link as QR
                                                      |
                                                      | phone scans QR
                                                      v
                                      RoutePacer PWA /open
                                                      |
                                                      | same-origin anonymous GET
                                                      v
                                      /api/handoffs/{token}
```

The phone never calls RouteTimer. RouteTimer needs no public hostname, inbound port, public TLS certificate, CORS policy, or anonymous endpoint. `localhost` and private LAN addresses never appear in the invocation URL.

RouteTimer's server-side relay upload works through normal NAT because it is an outbound HTTPS request. The relay and RoutePacer PWA share the `https://pacetracking.tqaentry.com` origin, so the phone-side payload fetch needs no CORS exception.

## 4. Contract v1

The RoutePacer invocation URL has these query parameters exactly once and in this order:

```text
https://pacetracking.tqaentry.com/open
  ?src=rt
  &v=1
  &payload=<absolute-https-relay-payload-url>
  &name=<route-name>
  &ts=<unix-milliseconds>
  &sig=<base64url-signature>
```

RouteTimer signs this UTF-8 byte sequence, with one line feed between fields and no trailing line feed:

```text
rt\n1\n{payload-absolute-uri}\n{name-or-empty}\n{unix-milliseconds}
```

The signature algorithm is ECDSA P-256 with SHA-256. Signature bytes use fixed-width IEEE-P1363 `r || s` format and unpadded base64url encoding. RouteTimer owns the private key; RoutePacer receives only the public JWK. The relay neither signs nor verifies invocation URLs.

Canonicalization occurs before query-string encoding:

- `payload-absolute-uri` is the absolute URL returned by the relay after RouteTimer validates it;
- `name-or-empty` is the unescaped .NET string, or the empty string;
- the timestamp uses invariant ASCII decimal digits from the injected `TimeProvider`;
- RouteTimer signs unescaped canonical values and then percent-encodes every query value once;
- RoutePacer parses and percent-decodes once before reconstructing the canonical sequence.

Both repositories contain the same fixed valid and tampered fixtures. Each fixture contains the public JWK, canonical UTF-8 text, relay payload URL, route name, timestamp, P1363 signature, full invocation URL, and fixture version. Test-only private key material may live under a test directory but never under `wwwroot` or production configuration.

## 5. Relay HTTP Contract

The public relay is implemented and deployed from the RoutePacer repository. RouteTimer depends only on this frozen HTTP contract.

### 5.1 Create a handoff

```http
POST https://pacetracking.tqaentry.com/api/handoffs
Authorization: Bearer <configured-relay-upload-key>
Content-Type: application/gpx+xml
Cache-Control: no-store

<raw timed GPX bytes>
```

Successful response:

```http
HTTP/1.1 201 Created
Content-Type: application/json
Cache-Control: no-store

{
  "payloadUrl": "https://pacetracking.tqaentry.com/api/handoffs/<43-character-token>",
  "expiresAt": "2026-08-27T12:10:00Z"
}
```

The relay chooses the token and fixes the lifetime at ten minutes. RouteTimer cannot request a longer lifetime. The upload body must be non-empty and no larger than 52,428,800 bytes. The relay returns `401` for a missing or invalid credential, `413` for an oversized body, `415` for a non-GPX media type, and `429` when its upload limit is exceeded.

### 5.2 Consume a handoff

```http
GET https://pacetracking.tqaentry.com/api/handoffs/<43-character-token>
```

The first valid request before expiry returns `200`, the exact original bytes, `Content-Type: application/gpx+xml`, `Content-Length`, `Cache-Control: no-store`, `Pragma: no-cache`, and `X-Content-Type-Options: nosniff`. Missing, malformed, expired, and previously consumed tokens all return the same `404` response. Consumption is atomic, so concurrent requests produce exactly one success.

The GET endpoint is anonymous because the random 256-bit token is its bearer credential. It is same-origin to RoutePacer. It never returns metadata about RouteTimer or the prediction.

### 5.3 Relay storage and logs

The RoutePacer repository adds a .NET 10 ASP.NET Core relay API backed by PostgreSQL shared by all relay replicas. The database stores plaintext GPX bytes but only a SHA-256 digest of each random token. Rows contain content, creation time, expiry, and consumption time. Expired rows and consumed rows older than the ten-minute lifetime are deleted automatically or opportunistically.

Plaintext storage is an explicit product decision. For up to ten minutes, the RoutePacer relay can read route and location data. RoutePacer privacy documentation must disclose this exception to the general on-device-storage rule. TLS protects the upload and download in transit, access is limited by the upload credential or random download token, and no backup may retain relay rows beyond their lifetime.

Application and ingress logs must not contain upload credentials, payload tokens, payload URLs, RoutePacer invocation query strings, signatures, route names, or GPX bytes. Aggregate result codes and byte counts are permitted. Access logging for `/api/handoffs/*` and `/open` query strings must be disabled or redacted.

## 6. Considered Approaches

### 6.1 Public RoutePacer relay plus QR — selected

The private RouteTimer API uploads outbound to a same-origin RoutePacer relay, then displays a signed link as a QR code. This works through NAT, opens the PWA on the intended phone, and needs no public RouteTimer ingress.

### 6.2 Public RouteTimer payload endpoint — rejected

The phone cannot resolve or reach the computer's `localhost`. A LAN URL works only on the same network and introduces mixed-content, certificate, CORS, and Private Network Access failures. A public tunnel or VPN would add an operational prerequisite that the likely user does not have.

### 6.3 Manual GPX transfer — retained as fallback

Downloading and transferring the timed GPX through AirDrop, Nearby Share, Files, or cloud storage remains reliable and requires no relay. It is not the primary flow because it is multi-step and platform-specific.

### 6.4 Inline GPX in a QR code — rejected

Real GPX routes exceed practical QR and browser URL sizes. Compression and animated multi-frame QR transfer would add substantial complexity and poor recovery behavior.

### 6.5 End-to-end encrypted relay content — deferred by explicit decision

Encrypting before upload would prevent relay operators from reading route data but requires a new contract version and decryption-key transfer. The selected first version stores plaintext for at most ten minutes and documents that privacy consequence.

## 7. RouteTimer Components

### 7.1 Relay client

`IRoutePacerRelayClient` accepts timed GPX bytes and returns `RoutePacerRelayGrant(string PayloadUrl, DateTimeOffset ExpiresAt)`. The production `RoutePacerRelayClient` uses a named `HttpClient`, sends the configured bearer credential only to the configured relay origin, streams with `ResponseHeadersRead`, and maps expected relay responses to stable service exceptions.

The client validates every successful relay response before it can be signed:

- `payloadUrl` is an absolute HTTPS URL;
- its origin exactly equals the configured RoutePacer relay origin;
- its path matches `/api/handoffs/{43-character-base64url-token}`;
- it has no query or fragment;
- `expiresAt` is later than the current `TimeProvider` value and no later than ten minutes plus thirty seconds in the future.

Redirects are disabled on the relay `HttpClient` so the bearer upload credential can never follow a redirect to another host.

### 7.2 Canonicalization and signing

`RoutePacerInvocationCanonicalizer` owns the exact Contract v1 byte sequence. `IRoutePacerInvocationSigner` signs those bytes. The production signer imports one configured P-256 private key at startup and explicitly selects `DSASignatureFormat.IeeeP1363FixedFieldConcatenation`.

Canonicalization is pure and fixture-driven. URL construction is separate so parameter ordering and single percent encoding are independently testable.

### 7.3 Handoff orchestration

`RoutePacerHandoffService.CreateAsync(Guid predictionId, CancellationToken)`:

1. refuses the operation when the feature is disabled;
2. reads the source through `PredictionQueryService.GetGpxSourceAsync`;
3. preserves the existing prediction-not-found and prediction-not-complete semantics;
4. creates exact timed GPX bytes with `PredictionGpxWriter.Write(source, timed: true)`;
5. uploads those bytes through `IRoutePacerRelayClient`;
6. canonicalizes and signs the validated relay payload URL, route name, and current timestamp;
7. returns the complete RoutePacer `/open` URL and relay expiry.

If upload, validation, or signing fails, no link is returned. A relay object created immediately before a later local failure simply expires within ten minutes.

### 7.4 RouteTimer API

A focused `RoutePacerEndpoints` class adds authenticated endpoints:

- `GET /api/routepacer/status` returns `{ "enabled": true|false, "routePacerOrigin": "https://pacetracking.tqaentry.com" }`;
- `POST /api/predictions/{id:guid}/routepacer-handoff` returns `{ "url": "...", "expiresAt": "..." }`.

Both endpoints retain the normal authenticated rider fallback policy. The existing same-origin mutation enforcement applies to the POST. RouteTimer adds no anonymous endpoint and no CORS policy.

Stable failures are:

- `404 prediction-not-found` when the prediction does not exist;
- `409 prediction-not-complete` when timed GPX cannot be produced;
- `503 routepacer-handoff-disabled` when configuration disables the feature;
- relay `401` maps to `502 routepacer-relay-authentication-failed`;
- relay `413` maps to `413 routepacer-payload-too-large`;
- relay `415` maps to `502 routepacer-relay-rejected-payload`;
- relay `429` maps to `503 routepacer-relay-rate-limited`, preserving a valid `Retry-After` value;
- timeouts, invalid responses, and relay `5xx` responses map to `502 routepacer-relay-unavailable` without echoing relay bodies or credentials.

### 7.5 RouteTimer client and QR flow

The prediction detail page requests RoutePacer status only after loading a prediction with stored segments. It renders the action only when the server reports the integration is enabled. Status failure hides the action without blanking the rest of the prediction page.

When the rider clicks **Open in PaceTracker**, RouteTimer creates the handoff and opens an inline dialog or panel containing:

- the locally generated QR code;
- the instruction **Scan this code with the phone you use for PaceTracker**;
- the expiry time and an expired state;
- **Copy link**, **Open on this device**, **Create a new code**, and **Close** actions;
- the existing timed-GPX download as the manual fallback.

The QR encodes only the signed RoutePacer invocation URL. QR generation runs locally in the browser using a pinned, vendored library and never calls an external QR service. The JavaScript wrapper follows RouteTimer's existing ES-module interop pattern. It renders into an owned element, clears previous output before rerendering, and exposes a pure function or independently testable boundary for URL validation.

The client treats the URL returned by RouteTimer as untrusted until it parses as HTTPS and its origin exactly equals the independently obtained `routePacerOrigin`. Copy and same-device navigation are disabled after expiry. Repeated creation clicks while a request is active do nothing. Page disposal cancels pending work and disposes the QR module.

The feature no longer pre-opens a blank browser tab, because the primary target is another device. **Open on this device** is an ordinary `target="_blank"` anchor rendered only after a valid signed link exists and therefore is not subject to an awaited popup call.

## 8. Configuration and Secrets

The RouteTimer API binds `RoutePacerHandoffOptions`:

```json
{
  "RoutePacerHandoff": {
    "Enabled": false,
    "RoutePacerBaseUrl": "https://pacetracking.tqaentry.com",
    "RelayUploadKey": "",
    "SigningPrivateKeyPem": ""
  }
}
```

Tracked configuration keeps the feature disabled and both secrets empty. Production supplies the relay upload key and P-256 private key through environment or secret-file configuration. Neither secret is returned to the browser, committed, logged, included in exception messages, or copied into Docker image layers.

Startup validation requires:

- an absolute HTTPS `RoutePacerBaseUrl` containing only an origin;
- when enabled, a non-empty relay upload key;
- when enabled, an importable ECDSA P-256 private key;
- when disabled, secrets may be absent so local development and tests start safely.

The relay upload key is a server-to-server authentication secret shared only by the private RouteTimer API and public relay. Unlike a symmetric invocation-signing key, it is never shipped in the RoutePacer WebAssembly client. The RoutePacer PWA contains only RouteTimer's public signing JWK.

RouteTimer deployment artifacts add the two secrets and feature flag without adding an inbound public route. The RouteTimer container needs ordinary outbound HTTPS/DNS access to `pacetracking.tqaentry.com`.

## 9. Testing Strategy

### Contract and crypto tests

- RouteTimer reproduces the fixed Contract v1 valid fixture byte-for-byte.
- Mutation of every signed field fails verification against the fixture signature.
- Parameter ordering, Unicode route names, spaces, reserved characters, and empty names round-trip without double encoding.
- RoutePacer consumes the same valid and tampered fixtures independently.
- Repository checks prove no private key is published to either browser app.

### Relay-client and service tests

- The client sends the exact timed GPX bytes, media type, no-store header, and bearer credential to the configured origin.
- Redirects are not followed.
- Foreign-origin, HTTP, malformed-token, expired, and overlong-expiry responses are rejected before signing.
- Relay `401`, `413`, `415`, `429`, `5xx`, timeout, and network failures map to stable safe errors.
- Missing and incomplete predictions retain existing 404/409 behavior.
- The injected `TimeProvider` controls timestamp and expiry validation.
- Logs and public exceptions contain no upload key, payload URL, signature, route name, or GPX bytes.

### API tests

- Status and handoff creation require an authenticated rider.
- Disabled configuration reports false and rejects creation.
- Enabled creation returns a valid signed URL and exact relay expiry.
- Existing prediction and timed-GPX endpoints remain unchanged.
- No response exposes the private key or relay upload credential.

### Client and browser tests

- bUnit covers hidden/visible status, creating, success, expired, duplicate-click, API failure, copy, recreate, close, and fallback-download states.
- JavaScript tests cover local QR rendering, rerender cleanup, invalid input refusal, and disposal.
- A browser test proves the QR contains the exact returned link, **Open on this device** uses the same link, and no external QR request occurs.
- A production-like acceptance test runs RouteTimer behind a private-only address, stubs or deploys the public relay, creates a handoff, fetches it from the RoutePacer origin, and proves the second fetch returns 404.

## 10. RoutePacer Repository Deliverable

The RouteTimer implementation plan produces a standalone prompt for the RoutePacer repository. That prompt must require the RoutePacer agent to use Superpowers and to reconcile its existing offline-first design and plan before implementation.

The prompt covers:

- adding a public same-origin handoff relay API and durable short-lived store;
- upload bearer authentication, request limits, atomic one-time consumption, expiry cleanup, and log redaction;
- retaining ECDSA Contract v1 verification with the relay URL as `payload`;
- fetching and importing the plaintext GPX on the phone;
- updating privacy documentation to disclose the ten-minute plaintext relay;
- adding deployment topology, secrets, health checks, database migration, and rollback;
- adding shared contract fixtures plus unit, API, concurrency, browser, and production-like tests;
- removing the obsolete assumption that RoutePacer fetches directly from a publicly hosted RouteTimer;
- preserving manual GPX import and offline tracking behavior.

The prompt must include this RouteTimer spec path and the frozen relay HTTP contract verbatim enough that the independently generated RoutePacer plan cannot choose incompatible endpoints, fields, status codes, token shape, lifetime, media type, or signature canonicalization.

## 11. Rollout and Operations

Deployment order is:

1. implement and deploy the RoutePacer relay and updated Contract v1 intake with intake disabled;
2. generate the relay upload credential and RouteTimer P-256 key outside both repositories;
3. configure the relay with the upload credential and configure RoutePacer with RouteTimer's public JWK;
4. configure private RouteTimer with the relay credential and private signing key while keeping handoff disabled;
5. run the shared valid/tampered fixtures and production-like relay flow;
6. enable RoutePacer intake, then enable RouteTimer handoff so the button appears;
7. smoke-test QR scanning on a real phone, first and second fetch, expiry, manual fallback, and private-only RouteTimer networking;
8. monitor aggregate status and byte counts only, with sensitive paths and query strings redacted.

Rollback disables RouteTimer handoff first, preventing new uploads and hiding the action, then disables relay uploads and RoutePacer intake. Existing objects expire within ten minutes. Database backups must exclude relay content or apply a retention shorter than the contract lifetime.

## 12. Acceptance Criteria

1. RouteTimer remains private and has no publicly addressable payload endpoint.
2. A rider can create a handoff on the computer, scan its QR code on the phone, and import the exact timed GPX into RoutePacer.
3. RouteTimer reaches the relay using outbound HTTPS and never exposes relay or signing secrets to its browser client.
4. RoutePacer verifies ECDSA P-256 Contract v1 without possessing RouteTimer's private key.
5. Relay payloads are absolute HTTPS URLs on the RoutePacer origin, store plaintext for no more than ten minutes, and succeed at most once under concurrent access.
6. The relay's plaintext processing and retention are accurately disclosed in RoutePacer privacy documentation.
7. Uploads require the configured server-side bearer credential; downloads require an unguessable 256-bit token.
8. QR generation is local, validates the target origin, and offers copy, same-device, recreate, expiry, and manual-download recovery paths.
9. Tokens, upload credentials, signatures, invocation URLs, private keys, route names, and GPX bytes are absent from application and ingress logs.
10. RouteTimer unit, API, client, JavaScript, and browser tests pass; the RoutePacer prompt requires corresponding relay, contract, concurrency, and production-like tests.

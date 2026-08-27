# Prompt: Add the Public RouteTimer Handoff Relay to RoutePacer

Copy the prompt below into a task opened in the RoutePacer repository.

---

Use Superpowers to redesign and plan the RoutePacer side of the RouteTimer handoff. Do not implement application code until the brainstorming design and written specification have passed their required approval gates.

Repository context:

- RoutePacer is a public Blazor WebAssembly PWA served from `https://pacetracking.tqaentry.com` and is expected to run on the rider's phone.
- RouteTimer usually runs privately in Docker on the rider's computer and must not gain a public inbound route.
- The old assumption that RoutePacer can fetch a payload from a publicly addressable RouteTimer host is invalid.
- Read RoutePacer's current `AGENTS.md`, `OFFLINE_FIRST_BLAZOR_CYCLING_APP_PLAN.md`, and `docs/superpowers/plans/2026-08-27-offline-first-route-pacer.md` before proposing changes.
- The authoritative coordinating design is RouteTimer's `docs/superpowers/specs/2026-08-27-open-in-pacetracker-design.md`. If the RouteTimer repository is available as a sibling, read it directly; otherwise ask me to provide it.
- Preserve RoutePacer's manual GPX/FIT import, IndexedDB library, offline app shell, and tracking behavior.

Required architecture:

1. Add a publicly hosted .NET 10 ASP.NET Core relay API under the same origin as the PWA. The production routes are `https://pacetracking.tqaentry.com/api/handoffs` and `https://pacetracking.tqaentry.com/api/handoffs/{token}`.
2. Back the relay with PostgreSQL shared by every relay replica. Do not use an in-memory cache for production.
3. RouteTimer uploads the raw timed GPX through outbound HTTPS. The phone never calls RouteTimer.
4. Store plaintext GPX for at most ten minutes. This plaintext exception is explicitly approved; update RoutePacer privacy documentation so it no longer claims that all route data always remains on-device.
5. Keep ECDSA P-256 RouteTimer Contract v1. RouteTimer owns the private key; RoutePacer publishes only the configured public JWK and verifies SHA-256 signatures in fixed-width IEEE-P1363 format.
6. RouteTimer presents the signed `/open` URL as a QR code. The phone scans it, RoutePacer verifies it, downloads the same-origin relay payload once, imports it, cleans the address bar, and offers the ready-to-start route.

Freeze this relay contract exactly in the RoutePacer design and plan:

```http
POST /api/handoffs
Authorization: Bearer <configured-relay-upload-key>
Content-Type: application/gpx+xml
Cache-Control: no-store

<raw timed GPX bytes>
```

Successful creation returns:

```http
HTTP/1.1 201 Created
Content-Type: application/json
Cache-Control: no-store

{
  "payloadUrl": "https://pacetracking.tqaentry.com/api/handoffs/<43-character-token>",
  "expiresAt": "<UTC ISO-8601 instant exactly ten minutes after creation>"
}
```

Creation rules:

- Generate 32 random token bytes and return them as 43-character unpadded base64url.
- Persist only `SHA-256(token)`, never the token itself.
- Accept a non-empty body no larger than 52,428,800 bytes.
- Return `401` for a missing/invalid upload credential, `413` for oversize, `415` unless the media type is `application/gpx+xml`, and `429` when upload rate limits are exceeded.
- Compare the configured bearer credential in constant time and redact `Authorization` from HTTP logs.
- Do not accept a caller-selected lifetime.

Consumption contract:

```http
GET /api/handoffs/<43-character-token>
```

- The first request before expiry returns the exact bytes with `application/gpx+xml`, `Content-Length`, `Cache-Control: no-store`, `Pragma: no-cache`, and `X-Content-Type-Options: nosniff`.
- Invalid, unknown, expired, and consumed tokens all return the same `404`.
- Consumption must be one atomic PostgreSQL operation so two concurrent requests produce one `200` and one `404`.
- Delete expired rows and consumed rows older than ten minutes automatically or opportunistically.
- Exclude the relay table from backups or use backup retention that cannot preserve content beyond the contract lifetime.

Contract v1 canonical bytes are UTF-8 with no trailing line feed:

```text
rt\n1\n{payload-absolute-uri}\n{name-or-empty}\n{unix-milliseconds}
```

The `/open` parser must require each query key exactly once, require `src=rt` and `v=1`, reject timestamps more than ten minutes old or more than sixty seconds in the future, require an HTTPS payload URL on the exact RoutePacer origin with path `/api/handoffs/{43-character-base64url-token}`, and verify the ECDSA signature before fetching. Fetch at most once, enforce the 52,428,800-byte limit using both `Content-Length` and a counting stream, require `application/gpx+xml`, import into IndexedDB, and remove the query string from browser history after terminal success or failure.

Logging and privacy requirements:

- Never log upload credentials, payload tokens, payload URLs, invocation query strings, signatures, route names, or GPX bytes.
- Disable or redact ingress access logs for `/api/handoffs/*` and `/open` query strings.
- Aggregate response codes, expiry cleanup counts, and byte counts are allowed.
- Document that the relay temporarily processes readable route/location data for at most ten minutes, after which it is deleted; route and ride data remain on-device after import.

Deployment requirements:

- Add the relay API, PostgreSQL migration, upload credential configuration, public-JWK configuration, health/readiness checks, same-origin reverse-proxy routing, secret provisioning, backup exclusion, deployment order, smoke test, and rollback steps.
- Keep relay upload and RouteTimer intake independently disableable.
- Deploy relay and intake disabled, configure the shared upload credential and RouteTimer public JWK, run production-like fixtures, enable RoutePacer first, then enable RouteTimer.

Testing requirements:

- Fixed valid and tampered cross-repository Contract v1 fixtures matching the RouteTimer fixture.
- Unit tests for strict parsing, canonicalization, expiry, future timestamps, signature verification, payload URL allowlisting, media type, and bounded reads.
- PostgreSQL tests for token hashing, expiry cleanup, restart/replica durability, and exactly-one-success concurrent consumption.
- API tests for every creation/consumption status, headers, authentication, body limits, no-store behavior, and safe indistinguishable 404s.
- bUnit/Playwright coverage for `/open` loading, success, safe failure, retry-before-consumption, URL cleanup, IndexedDB persistence, and ready-to-start navigation.
- A production-like test in which RouteTimer is reachable only privately, uploads outbound to the public relay, and a phone-context RoutePacer page imports the payload; the second fetch must return `404`.
- Repository scans proving no private signing key or relay upload credential is published under `wwwroot` and no sensitive values appear in logs.

Use the Superpowers brainstorming workflow to update the RoutePacer design/spec first. Explicitly present the new relay subsystem, plaintext privacy consequence, deployment topology, and alternatives for approval. After written-spec approval, use the writing-plans skill to revise the RoutePacer implementation plan with exact files, interfaces, TDD steps, commands, expected failures, and frequent commits. Respect RoutePacer's narrative contract for any decision-bearing pull request.

---

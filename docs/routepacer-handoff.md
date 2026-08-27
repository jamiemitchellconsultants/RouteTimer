# Open in PaceTracker — operator guide

RouteTimer can hand a finished prediction to PaceTracker on a phone by uploading its timed GPX to a
public RoutePacer relay and showing a signed, expiring link as a QR code. This document covers what
that means operationally: what is deployed where, what the privacy consequence is, how to generate
and provision the two secrets, the order to deploy in, how to verify it, and how to turn it off.

The feature is off by default. A RouteTimer that never enables it is unchanged.

## 1. Topology

```
  Rider's browser                RouteTimer (private)              RoutePacer (public origin)
  ───────────────                ────────────────────              ─────────────────────────
  Prediction page  ──POST──────► /api/predictions/{id}
                                   /routepacer-handoff
                                        │
                                        │  outbound HTTPS only
                                        └──────────POST /api/handoffs──────────►  relay stores
                                                                                  GPX + expiry
                                   ◄────── 201 { payloadUrl, expiresAt } ────────
                                        │
                                        │  signs Contract v1 with its P-256 key
  QR code  ◄────── { url, expiresAt } ──┘
       │
       │  rider scans with a phone
       └────────────────────────────────────────────► GET /open?…&payload=…&sig=…
                                                      PaceTracker verifies the signature,
                                                      fetches the payload once, imports the route.
```

The direction of every arrow matters. RouteTimer makes one **outbound** HTTPS call and serves
nothing to the phone. It gains no public hostname, no inbound port, no CORS policy, and no
anonymous endpoint. The phone never talks to RouteTimer at all.

## 2. Relay contract

Frozen, and shared byte-for-byte with the RoutePacer repository:

- `POST /api/handoffs` — bearer upload credential, `application/gpx+xml`, `Cache-Control: no-store`.
  Returns `201` with `payloadUrl` and `expiresAt`.
- `GET /api/handoffs/{43-character-base64url-token}` — anonymous; the random 256-bit token is the
  credential. Single-use, and a fixed ten-minute lifetime.

The signed invocation is `GET /open?src&v&payload&name&ts&sig`, in that order, each key once. The
signature covers UTF-8 `rt\n1\n{payload-url}\n{name}\n{unix-ms}` with no trailing newline, using
ECDSA P-256/SHA-256 in IEEE-P1363 form, unpadded base64url.

The interop vector both repositories test against is
`tests/RouteTimer.Services.Tests/RoutePacer/Fixtures/routepacer-contract-v1.json`. Its key is a
test key, published in this repository, and must never be used in a deployment. The RoutePacer-side
implementation is planned from
[`docs/superpowers/prompts/2026-08-27-routepacer-public-handoff-relay.md`](superpowers/prompts/2026-08-27-routepacer-public-handoff-relay.md).

## 3. Privacy consequence — read before enabling

**Relay content is plaintext, by explicit decision.** For up to ten minutes, the RoutePacer relay
holds the rider's route — the full geometry, so start and end locations included — in readable
form, and its operators can read it. TLS protects it in transit and the random token gates the
download, but this is not end-to-end encryption and must not be described as such to riders.

Consequences to hold to:

- RoutePacer's privacy documentation must disclose this exception to its on-device-storage rule.
- No backup may retain relay rows beyond their ten-minute lifetime.
- The relay stores only a SHA-256 digest of each token, never the token itself.
- A rider who does not want this has a first-class alternative: the timed GPX download, which the
  handoff panel links to directly and which needs no relay at all.

## 4. Generate and provision the secrets

Two secrets, both server-side only. Neither ever reaches the browser, a log, a response body, or an
image layer.

**Signing key.** Generate a P-256 private key in PKCS#8 form:

```bash
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt
```

Export the matching public JWK for RoutePacer's verifier — this is the only half that leaves
RouteTimer, and it is public:

```bash
openssl ec -in key.pem -pubout
```

Give RoutePacer the public key as a JWK with `"kty":"EC"`, `"crv":"P-256"`, and the base64url `x`
and `y` coordinates. RoutePacer verifies every `/open` request against it.

**Relay upload credential.** Provisioned by RoutePacer, not generated here. It authenticates
RouteTimer to `POST /api/handoffs` and nothing else. Rotate it by provisioning the new value on the
relay, updating RouteTimer's environment, and restarting RouteTimer.

Both go in an untracked, permission-restricted `deploy/.env` (`chmod 600`), never in Compose YAML,
an issue, a screenshot, or a log bundle:

```
ROUTEPACER_HANDOFF_ENABLED=true
ROUTEPACER_RELAY_UPLOAD_KEY=<credential from RoutePacer>
ROUTEPACER_SIGNING_PRIVATE_KEY_PEM='<the whole PEM, BEGIN and END lines included>'
```

The PEM is multi-line: wrap the entire value in single quotes, opening before the first line and
closing after the last. Compose reads a quoted multi-line value literally.

Startup validation is fail-closed. With `Enabled` true and either value empty — exactly what a
missing `.env` produces — the container refuses to start rather than booting into a feature that
cannot work. The signing key is also curve-checked at startup, so a key RoutePacer could never
verify is caught before any rider sees it.

## 5. Deployment order

The relay must exist before RouteTimer is told to use it, or every handoff fails on the first
click.

1. Deploy the RoutePacer relay and its `/open` intake to the public origin.
2. Provision the upload credential on the relay; give RoutePacer the public JWK.
3. Verify the contract fixtures match byte-for-byte in both repositories.
4. Write `deploy/.env` on the RouteTimer host and `chmod 600` it.
5. `docker compose -f deploy/docker-compose.yml up -d`.
6. Confirm `curl -f https://<hostname>/health/ready` returns `200` — a fail-closed validation error
   shows up here as a container that will not start.
7. Run the smoke test below.

Nothing in this sequence adds a Compose `ports:` entry or a Caddy route. If a step seems to call for
one, stop: the design has been misread.

## 6. Smoke test

On a real phone, not an emulator:

1. Open a completed prediction in RouteTimer. "Open in PaceTracker" should be visible.
2. Click it. A QR code appears with an expiry time.
3. Scan it with the phone's camera. PaceTracker opens and imports the route.
4. Check the route's pacing is present — times, not just geometry.
5. **Fetch the payload URL a second time.** It must return `404`. Single-use is what limits the
   exposure window; if a second fetch succeeds, stop and fix the relay before going further.
6. Create another handoff, wait past ten minutes, then open it. The panel must show the expired
   state and offer a new code, and the payload URL must return `404`.

## 7. Monitoring

Log and alert on aggregates only: handoff counts, result codes, byte totals, relay latency.

Never log, and redact at the ingress if necessary: the upload credential, payload tokens, payload
URLs, `/open` query strings, signatures, route names, and GPX bytes. Access logging for
`/api/handoffs/*` and for `/open` query strings must be disabled or redacted. The relay client's
`Authorization` header is already redacted in RouteTimer's own HTTP logging.

## 8. Rollback — disable RouteTimer first

Order matters, and this direction leaves no broken state:

1. Set `ROUTEPACER_HANDOFF_ENABLED=false` and restart RouteTimer. The action disappears from the
   prediction page. GPX downloads, the visualization, and Garmin are untouched — they share no code
   path with the handoff.
2. Let any outstanding relay payloads expire — at most ten minutes.
3. Then disable the RoutePacer intake if that is also intended.

Disabling the relay first instead would leave RouteTimer offering an action that fails at the
relay, which riders see as an error rather than as a feature that is simply not available.

Neither secret needs to be removed to disable the feature, but rotate the upload credential if the
reason for the rollback was a suspected leak.

## 9. Readiness gate — current status: **NO-GO**

Checked 2026-08-28 against RouteTimer commit on `feat/open-in-pacetracker` and the RoutePacer
working tree. **Do not enable in production until the blocking item below is resolved.**

### Blocking: RoutePacer rejects the contract's `src` value

Both specs freeze the first query key as `src=rt`, and both repositories' canonicalizers sign the
literal `rt` as the first canonical line — those agree. But RoutePacer's invocation parser requires
the *query parameter* to equal `RouteTimer`:

```
src/RoutePacer.App/Invocation/InvocationParser.cs
    values["src"] != "RouteTimer"   // rejects the contract's src=rt
```

Every link RouteTimer produces would be refused as `Invalid invocation query` before its signature
is ever checked. RoutePacer's own `InvocationCanonicalizer` uses `rt`, so the parser contradicts
the format it verifies against — the defect is internal to RoutePacer and the fix belongs there,
not here. RouteTimer emits what both specs and the shared canonical form require.

### Verified as agreeing

- Canonical byte sequence: `rt\n1\n{payload}\n{name}\n{unix-ms}`, no trailing newline.
- Query key set and order: `src`, `v`, `payload`, `name`, `ts`, `sig`, each once.
- Payload URL shape: HTTPS, same origin, `/api/handoffs/{43-char base64url}`, no query or fragment.
- Signature encoding: 64-byte IEEE-P1363, unpadded base64url, rejected if either differs.
- RouteTimer's interop fixture is internally consistent — its recorded signature verifies against
  its own public key over its own canonical bytes.

### Not yet run

RoutePacer holds no mirrored copy of
`tests/RouteTimer.Services.Tests/RoutePacer/Fixtures/routepacer-contract-v1.json`, so the
byte-for-byte fixture comparison and the mutated-field rejection tests are outstanding. Steps 2 and
3 of the readiness gate — the production-like private-to-public flow on a real phone, the
single-use `404` on second fetch, the ten-minute expiry check, and the log inspection — need a
deployed relay and have not been performed.

Record only aggregate status, tested commit identifiers, origins, and timestamps when this gate is
re-run. Never the fixture private key, production secrets, live tokens, signed URLs, route names,
or GPX content.

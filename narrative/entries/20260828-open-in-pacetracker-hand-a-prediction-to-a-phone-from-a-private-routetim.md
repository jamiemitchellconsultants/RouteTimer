---
date: 2026-08-28
slug: open-in-pacetracker-hand-a-prediction-to-a-phone-from-a-private-routetim
title: "Open in PaceTracker: hand a prediction to a phone from a private RouteTimer"
summary: "Invert the direction. RouteTimer makes one **outbound** HTTPS call to a public RoutePacer relay, uploads the timed GPX, and receives a single-use payload URL with a fixed ten-minute lifetime."
kind: product
status: accepted
sequence: 2026-08-28T07:05:04.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/33; merge commit 86894717d228742a0d9b5151d0da3f9e500893ec"
---

## Context

A rider running RouteTimer privately — on a laptop, behind NAT, with no public ingress — had no way
to get a finished prediction onto the phone that would actually be used on the ride, short of
downloading a GPX and transferring it by hand.

The obvious fix, serving the payload from RouteTimer, does not survive contact with the constraint.
A phone cannot resolve the computer's `localhost`; a LAN address works only on the same network and
drags in mixed-content, certificate, CORS, and Private Network Access failures; and a tunnel or VPN
adds an operational prerequisite the likely user does not have. Inlining a real GPX route into a QR
code exceeds practical QR and URL sizes.

The load-bearing constraint was therefore that **RouteTimer must stay private**: whatever was built
could not add a public hostname, an inbound port, a CORS policy, or an anonymous endpoint.

## Decision

Invert the direction. RouteTimer makes one **outbound** HTTPS call to a public RoutePacer relay,
uploads the timed GPX, and receives a single-use payload URL with a fixed ten-minute lifetime. It
signs RoutePacer Contract v1 over that URL and displays the result as a QR code the phone scans.
Every arrow points outward from the private host.

Contract v1 is frozen and shared byte-for-byte with the RoutePacer repository through a checked-in
interop fixture: canonical bytes `rt\n1\n{payload}\n{name}\n{unix-ms}` with no trailing line feed,
signed ECDSA P-256/SHA-256 in IEEE-P1363 form, unpadded base64url, with the six query keys emitted
once each in a fixed order.

Decisions worth recording:

- **Relay content is plaintext, deliberately.** End-to-end encryption was considered and deferred:
  it needs a new contract version and a decryption-key transfer. The consequence is disclosed
  rather than hidden — see below.
- **The manual timed-GPX download is retained as a first-class fallback**, linked directly from the
  handoff panel. It is the answer when the relay is down, the code has expired, or the rider simply
  prefers not to use a relay.
- **QR generation is entirely local.** `qrcode` is bundled by esbuild into a vendored ES module; no
  hosted QR service ever sees a rider's route URL.
- **The client re-validates the link independently.** The browser checks the returned URL against
  the origin the server reported before rendering a scannable code, so a bad response cannot direct
  a phone elsewhere.
- **Fail closed everywhere.** Startup validation rejects an enabled-but-unconfigured deployment; a
  failed status call hides the action rather than offering one that cannot work; relay failures map
  to stable public error codes that never echo relay bodies, URLs, credentials, or route names.

## Consequences

**Privacy.** For up to ten minutes the RoutePacer relay holds the route — full geometry, so start
and end locations included — in readable form, and its operators can read it. TLS protects transit
and a random 256-bit token gates the download, but this is not end-to-end encryption and must not
be described to riders as such. RoutePacer's privacy documentation must disclose this exception to
its on-device-storage rule, and no backup may retain relay rows beyond their lifetime. The rider's
opt-out is the timed GPX download.

**A cross-repository contract now exists.** RouteTimer and RoutePacer are coupled through Contract
v1. The readiness gate caught a real mismatch on first run: RoutePacer's parser required
`src=RouteTimer` while its own canonicalizer signed `rt`, so every link this branch produced would
have been refused before its signature was checked. Fixed in RoutePacer
(jamiemitchellconsultants/RoutePacer#10) and re-verified here — the fixture is mirrored
byte-for-byte, RoutePacer parses and verifies this repository's signed URL, rejects it with each
signed field mutated, and now refuses the old spelling. The fixture is the guard against future
drift; changing either side without it is how this breaks again.

**Two new server-side secrets** — the relay upload credential and the ECDSA signing key — live only
in environment configuration. Losing or rotating the signing key requires RoutePacer to be given
the new public JWK, or every link stops verifying.

**Deliberately left open:** readiness-gate steps 2 and 3 are unrun and need a deployed relay and a
real phone, so this ships disabled; end-to-end encryption is deferred to a future contract version;
and the relay's ten-minute lifetime is fixed by RoutePacer, not negotiable per handoff.

---
date: 2026-08-29
slug: chore-remove-the-open-in-pacetracker-handoff
title: "chore: remove the Open in PaceTracker handoff"
summary: "Remove the endpoints, options and validator, the relay client, both invocation signers, the QR interop and component, the contracts, the six error codes, and every test covering them. `qrcode` and `esbuild` go with it."
kind: product
status: accepted
sequence: 2026-08-29T06:11:43.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/36; merge commit b69223dc1da91115e314e024a683c8237a51c536"
---

## Context

This reverses `plan-private-to-phone-pacetracker-relay-handoff` and the implementation entry that followed it. Per `AGENTS.md` both stand unaltered; this is a `correction` citing them by slug.

The handoff uploaded a timed GPX to RoutePacer's relay and rendered a signed, expiring QR code so a phone could fetch it. It existed to save one file transfer.

It cannot serve anyone but this deployment. RoutePacer's relay authenticates exactly one upload credential (`HandoffRelayOptions.UploadCredential`, a single string) and verifies exactly one signing key (`RouteTimerInvocationOptions.PublicKeyJwk`, a single key). A second RouteTimer would need the shared credential — making it public and the relay an open file drop — and the private signing key, which would defeat the signature entirely. Contract v1 is frozen at six query keys and rejects any additional one, so there is nowhere to put a tenant identifier without a v2 across both repositories.

RouteTimer is a public repository, and a feature only its author can use does not belong in one.

## Decision

Remove the endpoints, options and validator, the relay client, both invocation signers, the QR interop and component, the contracts, the six error codes, and every test covering them.

`qrcode` and `esbuild` go with it. The QR was their only consumer, so `scripts/qrcode-entry.mjs` is deleted and `build-vendor.mjs` returns to plain copies of Leaflet and Chart.js — the bundler existed solely to turn qrcode's CommonJS into one browser ES module.

`RUNBOOK.md` and `README.md` are rewritten rather than trimmed. Both are rider-facing and both described an action riders can no longer take. "Sending a prediction to your phone" now presents the file route as the answer rather than the fallback, and says plainly why the relay went. Six troubleshooting entries — relay authentication, rate limiting, availability, expiry, invalid signature, action missing — are deleted with the thing they diagnosed.

## Consequences

Riders keep the capability. **Download GPX with predicted times** and move the file the way files already move. That needs no relay, no credential and no signing key, and works for every deployment rather than one. It was already the documented fallback; it is now simply the route.

RouteTimer no longer holds a signing key or an upload credential, so `deploy/.env` loses three variables and the deployment loses a class of secret entirely. Anyone who provisioned a P-256 key for this can discard it.

RoutePacer's side is removed in its PR #18, which also deletes PostgreSQL — the relay was the only thing using it there. The frozen contract, its fixture, and the coordinated rollout document go with it, so restoring this feature means starting from the multi-tenant design that was never built.

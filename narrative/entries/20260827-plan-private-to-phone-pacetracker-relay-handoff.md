---
date: 2026-08-27
slug: plan-private-to-phone-pacetracker-relay-handoff
title: "Plan private-to-phone PaceTracker relay handoff"
summary: "Use a public, same-origin RoutePacer handoff relay. Private RouteTimer generates the timed GPX and uploads it outbound over HTTPS using a server-side relay credential."
kind: product
status: accepted
sequence: 2026-08-27T13:40:09.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/24; merge commit f5780e38c5127ba193ef2da05cac784b5a5405a1"
---

## Context

RouteTimer will normally run in Docker on the rider's local computer, while RoutePacer is a public PWA used on the rider's phone. The earlier plan assumed that the phone could fetch a short-lived payload from a publicly addressable RouteTimer host and that clicking the action would open the intended device. Neither assumption holds for a private localhost deployment: the phone cannot reach the computer's localhost, LAN access is unreliable across HTTPS/private-network boundaries, and a desktop click does not open the phone.

## Decision

Use a public, same-origin RoutePacer handoff relay. Private RouteTimer generates the timed GPX and uploads it outbound over HTTPS using a server-side relay credential. The relay stores plaintext GPX for at most ten minutes behind a 256-bit, single-use token. RouteTimer validates the returned relay URL, signs RoutePacer Contract v1 with ECDSA P-256, and displays the signed RoutePacer `/open` URL as a locally generated QR code.

Rejected alternatives are a public RouteTimer payload endpoint, LAN/private-network browser fetches, inline GPX QR payloads, and HMAC verification in WebAssembly. Manual GPX transfer remains the fallback. End-to-end relay encryption is explicitly deferred; plaintext temporary processing is documented as a product consequence.

## Consequences

RouteTimer remains private and gains no anonymous endpoint, inbound port, public hostname, or CORS policy. RoutePacer must gain a public ASP.NET Core/PostgreSQL relay, upload authentication, atomic one-time consumption, retention cleanup, privacy disclosure, deployment controls, and updated Contract v1 intake. The two repositories coordinate through a frozen HTTP contract and shared valid/tampered fixtures.

The relay can read route/location data during the ten-minute window, so logs and backups must exclude payload contents, credentials, tokens, signed URLs, route names, and invocation queries. Rollout requires configuring a shared relay upload credential plus RouteTimer's private signing key and RoutePacer's corresponding public JWK before enabling either side.

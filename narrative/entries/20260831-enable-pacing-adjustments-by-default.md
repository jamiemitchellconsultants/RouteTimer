---
date: 2026-08-31
slug: enable-pacing-adjustments-by-default
title: "Enable pacing adjustments by default"
summary: "Enable the parent pacing-adjustment gate and all five delivered strategy gates in the default API configuration: segment-specific gains, NP/IF target, time target, RPE/zone shift, and variable match-burning."
kind: product
status: accepted
sequence: 2026-08-31T05:47:27.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/46; merge commit d817bf385e42f9de77071dab5bfdf14888593099"
---

## Context

The pacing-adjustment architecture, API, five strategy implementations, UI editors, visualization, validation, and backtesting are present, but the checked-in API configuration still disables the parent capability and every individual strategy. As a result, the capability endpoint reports the feature unavailable and the client hides adjustment creation despite the completed vertical slices.

## Decision

Enable the parent pacing-adjustment gate and all five delivered strategy gates in the default API configuration: segment-specific gains, NP/IF target, time target, RPE/zone shift, and variable match-burning. Keep the existing per-strategy override mechanism and request-size, rule-count, and phase-count safety limits unchanged. A partial rollout was rejected because all five strategies are delivered and the requested product state is to enable pacing adjustments rather than expose only a subset.

## Consequences

Default deployments now advertise pacing adjustments and allow riders to create each supported adjustment type from successful baseline predictions. Operators can still disable the entire capability or individual strategies through configuration overrides. Adjustment calculations may increase worker CPU usage when riders submit them; the existing bounds and independent feature flags remain available for operational control. API regression tests now protect the intended default while explicitly testing both parent-level and per-strategy rollback behavior.

---
date: 2026-08-31
slug: docs-design-weather-aware-training-interpretation
title: "docs: design weather-aware training interpretation"
summary: "Use Open-Meteo archive observations for all existing and future training rides, persisted beside immutable activity evidence and resolved by route position and sample time."
kind: product
status: accepted
sequence: 2026-08-31T10:25:26.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/48; merge commit 312986ab56185d7c005667904f2cc9bcdf92f4c6"
---

## Context

Training rides are currently interpreted as though they occurred in calm, dry reference conditions. Wind, air density, and precipitation can therefore bias physical calibration, descent learning, and validation. The feature must correct historical training evidence without changing recorded samples or applying weather automatically to ordinary predictions.

## Decision

Use Open-Meteo archive observations for all existing and future training rides, persisted beside immutable activity evidence and resolved by route position and sample time. Rebuild rider models from dry calm-reference interpretations of wind, temperature, pressure, and precipitation. Keep ordinary predictions calm and dry. Offer current weather only through an opt-in timed-GPX download that performs a transient route-time forecast recomputation and persists nothing.

Rejected alternatives include scaling recorded watts, applying current weather to stored predictions, silently falling back to baseline bytes after forecast failure, and querying weather at every recorded point.

## Consequences

Representative route coordinates and times are disclosed to the configured Open-Meteo-compatible provider. Model publication waits for otherwise-eligible pending weather evidence while the previous model remains active. Failed or unavailable weather evidence is visible and excluded. Legacy predictions retain baseline downloads but must be recreated before forecast adjustment. The additive weather schema and historical observations require backup and operational monitoring, while forecast results remain ephemeral.

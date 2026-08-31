---
date: 2026-08-31
slug: docs-plan-ai-supported-route-predictions
title: "docs: plan AI-supported route predictions"
summary: "Adopt a local hybrid model that learns a bounded route-level effort multiplier in log space and then reruns the existing physics predictor. Keep the deterministic result as the permanent baseline and fallback."
kind: product
status: accepted
sequence: 2026-08-31T12:14:07.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/52; merge commit 6b305e25b1049cde5fb0ed47e4dec81a4957fd6e"
---

## Context

RouteTimer currently predicts route time from a deterministic rider profile and cycling-physics model. With enough varied, weather-ready training rides, it can add rider-specific learning, but only if chronological validation, route-specific support, history freshness, fallback behaviour, privacy, and prediction provenance are designed together. The implementation sequence also needs enough explicit contracts and test boundaries for a smaller coding model to execute safely one task at a time.

## Decision

Adopt a local hybrid model that learns a bounded route-level effort multiplier in log space and then reruns the existing physics predictor. Keep the deterministic result as the permanent baseline and fallback. Gate publication through nested chronological whole-ride validation, gate serving through route similarity, and let Today fall back to AI Typical before deterministic prediction. Deliver the work as 15 ordered, independently committed and pushed tasks after the weather-aware training plan is complete. Reject hosted or opaque models, direct end-to-end time correction, random ride splitting, and a user-facing AI enable switch.

## Consequences

No runtime behaviour changes in this PR. The repository gains an executable implementation sequence with fixed feature/version contracts, immutable model and prediction provenance, local-only training, staged rollout, restrained readiness feedback, and explicit review checkpoints. Implementation becomes deliberately incremental and will create more small commits and migrations. AI support may remain unavailable for riders or routes without enough varied evidence, while deterministic prediction remains available. Algorithm thresholds are fixed for the first version and can be revised later only through a new recorded decision.

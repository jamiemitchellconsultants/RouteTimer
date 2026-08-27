---
date: 2026-08-27
slug: docs-add-pacing-strategy-implementation-plans
title: "docs: add pacing strategy implementation plans"
summary: "Document five strategy designs in-repo: 1. Segment-Specific Gains 2. Normalized Power / IF Target 3. Time Target Mode 4. RPE / Zone Shift 5."
kind: product
status: accepted
sequence: 2026-08-27T17:00:14.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/26; merge commit bfd566f415624d116f4db79339563a531f311f29"
---

## Context

RouteTimer needs an implementation-ready plan for expanding route prediction beyond baseline power-model simulation into explicit pacing modes. We needed a structured decision record covering how each strategy fits the existing prediction pipeline (`PredictionSubmissionService` → `PredictionJobHandler` → `RoutePredictor`), what persistence and contract changes are required, and how to roll these changes out safely without destabilizing current behavior.

## Decision

Document five strategy designs in-repo:

1. Segment-Specific Gains
2. Normalized Power / IF Target
3. Time Target Mode
4. RPE / Zone Shift
5. Variable Match-Burning

Plus one cross-cutting rollout document that defines shared infrastructure (strategy union/contracts, adjusted-segment persistence, handler abstraction, feature flags), migration sequencing, and recommended delivery order.

## Consequences

The repo now has a concrete, reviewable blueprint for staged implementation, including explicit trade-offs and edge-case handling before code execution begins. This improves delivery alignment across backend/API/UI workstreams and reduces design drift, but it also establishes architecture expectations (strategy metadata persistence, adjusted result shape, and shared strategy plumbing) that implementation PRs must now follow or consciously revise through a new narrative correction path.

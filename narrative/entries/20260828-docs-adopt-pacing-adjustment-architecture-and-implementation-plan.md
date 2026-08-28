---
date: 2026-08-28
slug: docs-adopt-pacing-adjustment-architecture-and-implementation-plan
title: "docs: adopt pacing adjustment architecture and implementation plan"
summary: "Adopt an immutable baseline with multiple append-only `PredictionAdjustment` children."
kind: product
status: accepted
sequence: 2026-08-28T03:29:55.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/28; merge commit cfc7de06bd46b0bb824b39f5e45becfcffd2e8c1"
---

## Context

The accepted `docs-add-pacing-strategy-implementation-plans` entry documented five useful strategies, but its strategy-at-submission and single-adjusted-result framing does not preserve one trusted baseline from which a rider can create, retain, revisit, and compare multiple alternatives. The earlier drafts also left important simulation details ambiguous, including route context for policies, full-physics search behavior, and W-prime recovery.

This pull request carries the required explicit `kind: correction` fragment for that reversal. The merge-time narrative records the additional product decision to adopt the detailed architecture and staged implementation sequence.

## Decision

Adopt an immutable baseline with multiple append-only `PredictionAdjustment` children. Create adjustments only after a baseline succeeds, apply exactly one strategy to each child, use the baseline's captured route/model/profile inputs, and keep baseline history, exports, and Garmin actions primary and backward compatible.

Implement the work through the plan's shared-infrastructure foundation and independently releasable strategy slices: segment gains, time target, NP/IF target, zone targeting, and variable match-burning. Require full sequential-physics simulations for search candidates, exponential W-prime reconstitution, default-off feature flags, and review checkpoints with fresh verification.

## Consequences

Riders can always return to the primary prediction and try different adjustment strategies without replacing earlier results or reprocessing the route. Existing succeeded predictions remain eligible, while failed, deleted, or disabled adjustments cannot mutate their baseline or siblings.

The implementation adds a dedicated adjustment aggregate, durable job type, nested API, strategy reports, adjusted segments, and comparison UI. It also increases schema, worker, testing, and operational complexity; full-simulation searches cost more CPU, match-burning remains an estimate, composed strategies and adjusted exports remain out of scope, and every capability stays disabled until its vertical slice passes its rollout gate.

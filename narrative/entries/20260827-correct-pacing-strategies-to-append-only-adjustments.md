---
date: 2026-08-27
slug: correct-pacing-strategies-to-append-only-adjustments
title: "Correct pacing strategies to append-only adjustments"
summary: "Keep each prediction as an immutable baseline and model pacing strategies as multiple append-only child adjustments created only after the baseline succeeds."
kind: correction
status: accepted
sequence: 2026-08-27T20:49:16.000Z
evidence: "docs/superpowers/specs/2026-08-27-pacing-strategy-adjustments-design.md"
---

## Context

The accepted entry `docs-add-pacing-strategy-implementation-plans` recorded five useful pacing
strategies but framed a strategy as part of prediction submission and provided one secondary
adjusted result. That architecture does not preserve the baseline as the stable primary result from
which a rider can try and revisit multiple independent alternatives. The drafts also proposed a
power wrapper that lacks the route context needed for distance rules and used a fixed linear
W-prime recovery approximation while naming the exponential Skiba model.

## Decision

Keep route submission baseline-only and immutable. After a baseline succeeds, allow multiple
append-only `PredictionAdjustment` children, each containing exactly one strategy and using the
baseline's captured route segments, rider model, profile, and assumptions. Add a full-segment
power-target policy, run search candidates through the complete sequential physics simulation, and
store typed strategy reports plus adjusted segments beneath each child.

Use exponential W-prime reconstitution for match-burning, remove the undefined EvenEffort
time-target mode, and keep existing baseline contracts, GPX exports, Garmin actions, warnings, and
history primary and backward compatible.

## Consequences

Riders can return to one trusted baseline, create multiple strategy alternatives without uploading
or processing the route again, and delete a child without affecting its siblings. Existing
succeeded predictions remain eligible because their persisted segments contain the simulation
inputs.

Implementation gains a distinct adjustment lifecycle, job type, persistence aggregate, nested API,
feature flags, strategy editors, and comparison UI. Full simulation searches cost more CPU than
post-hoc scaling but preserve duration-band and sequential-physics correctness. Adjusted GPX/Garmin
export and composed strategies remain outside this feature.

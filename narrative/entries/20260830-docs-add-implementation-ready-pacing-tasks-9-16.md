---
date: 2026-08-30
slug: docs-add-implementation-ready-pacing-tasks-9-16
title: "docs: add implementation-ready pacing tasks 9-16"
summary: "Keep the original plan files unchanged and add a sibling refined-tasks directory."
kind: product
status: accepted
sequence: 2026-08-30T11:55:21.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/41; merge commit fbe95f76153b18e4392cf05865f9edc960616c4f"
---

## Context

The original Tasks 9-16 were milestone summaries and left critical details to the implementer, including search tie-breaking, fractional-second normalized-power behavior, concrete strategy interfaces, stable sub-field wire literals, lifecycle outcomes, and current visualization extension points. Tasks 1-8 also exposed architectural patterns and deviations that the remaining plan needed to carry forward explicitly.

## Decision

Keep the original plan files unchanged and add a sibling refined-tasks directory. Define deterministic interfaces and behavior for bounded search, normalized power, time targeting, NP/IF targeting, zone resolution, match-burning, one-adjustment visualization, lifecycle hardening, and rollout verification. Split large work into commit-sized checkpoints and correct stale assumptions: match-burning refinement follows changed phase membership, visualization uses RouteProfiles and route-visualization modules, baseline deletion cancels active adjustment jobs, and Narrative verification does not depend on a machine-specific temporary CLI path.

Rejected alternatives were overwriting the historical task files, leaving algorithmic choices to each executor, or introducing new production abstractions solely to make the plan shorter.

## Consequences

A smaller local model can execute one checkpoint with explicit inputs, outputs, tests, files, commands, and stop conditions, reducing architectural drift and invented behavior. The refined plan is longer and records implementation choices that future code should either follow or deliberately correct. Original planning history remains available for comparison, and no production code or feature flag changes in this PR.

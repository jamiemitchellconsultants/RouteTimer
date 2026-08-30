---
date: 2026-08-30
slug: implement-adjustment-visualization-and-lifecycle-hardening
title: "feat: add adjustment visualization overlays and lifecycle hardening"
summary: "Deliver Task 14, which had not been started, and Task 15's missing verification; a published adjustment could be overwritten by a duplicate delivery."
kind: product
status: accepted
sequence: 2026-08-30T18:40:00.000Z
evidence: "Branch feature/pacing-strategy-adjustments-tasks-09-16-17416437996458993256; follows [[fix-review-findings-in-pacing-tasks-9-16]]"
---

## Context

The tasks 9-16 delivery claimed the whole range. Tasks 9 through 13 were there. Task 14 was not started at all: no visualization file was touched, so an adjustment could be created and stored but never seen against its baseline. Task 15 had its three production changes - the stored-JSON byte guard, annotation validation at the job boundary, and cancelling child jobs when a baseline is deleted - but none of its verification: the failure-test file was never created and all five listed test files were untouched.

Writing that verification turned up two defects the production code still had. `SegmentGainsRequestMapper` dereferenced a null `rules` collection, so a request that simply omitted it returned 500 rather than a field error - the one mapper that had not been given the null guard its siblings carry. And `TryPublishAsync` guarded only on the job still being Running with a matching worker, which is exactly the state a duplicate delivery arrives in, so a second delivery overwrote an already-succeeded child. That contradicts Task 15's own acceptance criterion that completed children are never mutated.

## Decision

Implement Task 14 as specified, in its three checkpoints, touching only the nine files it lists. Alignment happens once in C#: `PredictionVisualization` sorts the adjustment, requires exact sequence equality, and builds one dictionary, so the readout and the charts never join inside a render loop. A mismatch keeps the baseline map, charts, and readout and passes an empty list on, so no comparison interop runs. The baseline-only interop call keeps its four arguments byte-for-byte and comparison uses a new five-argument export.

Comparison downsampling keeps the ends and both sides of every zone or phase change, and keeps all mandatory points even when they exceed the 1500-point cap - a semantic boundary is worth more than the display limit. Because downsampling can drop the selected sequence, the chart cursor now falls back to the nearest surviving point rather than snapping to the route start.

For Task 15, add the verification and fix what it exposed: null and null-entried collections become field errors, and a terminal child rejects republication instead of being overwritten. Two rows of the required-state table were already covered by Tasks 1-8 tests and were left alone. `PredictionDeletionService` is a pass-through with full coverage, so the deletion behaviour is pinned in the repository where it lives rather than duplicated at the service.

## Consequences

An adjustment is now visible: baseline and adjustment power and speed as two lines, with deltas, segment-time deltas, and zone, phase, and W-prime annotations in one tooltip block, and the same figures in the selected-segment readout. Elevation, gradient, geometry, exports, and the map stay baseline-primary.

The lifecycle is pinned where it was only asserted: a stored strategy that no longer parses, is too large, or fails its constructor is a strategy problem; a search with no candidate is a result problem; cancellation stays a cancellation. A baseline delete cancels every active child job and leaves completed ones for audit. A baseline that succeeded before the adjustments migration existed can still be adjusted from its retained segments.

Two behaviours changed that were not merely untested: a duplicate delivery now returns false, and a request omitting a collection now gets a 400. Anything that depended on republishing over a succeeded child will stop working, which is the intent.

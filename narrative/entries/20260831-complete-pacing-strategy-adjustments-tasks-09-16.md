---
date: 2026-08-31
slug: complete-pacing-strategy-adjustments-tasks-09-16
title: "Complete pacing strategy adjustments tasks 09-16"
summary: "Complete tasks 14-16, restore the jobs-first lock order in baseline deletion, and floor a power-limited segment instead of failing the whole route."
kind: product
status: accepted
sequence: 2026-08-31T04:46:11.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/44; merge commit bb61765056e74bd7511b808777b55b2ffcbe6b76"
---

## Context

The tasks 09-16 delivery claimed the whole range. Tasks 09-13 were present. Task 14 touched no visualization file at all, so an adjustment could be created and stored but never seen against its baseline. Task 15 had its three production changes — the stored-JSON byte guard, annotation validation at the job boundary, and cancelling child jobs when a baseline is deleted — but none of its verification. Task 16 had produced its two new files without finishing either, and `06-cross-cutting-rollout.md` still described the design that the accepted append-only correction replaced: a strategy carried on submission, a `POST /api/predictions/paced` endpoint, a `prediction_adjusted_segments` table, strategy columns on `predictions`. An operator following it would have looked for endpoints and columns that do not exist.

Writing the missing verification found real defects rather than only gaps. `SegmentGainsRequestMapper` dereferenced a null `rules` collection, so a request that omitted it returned 500 instead of a field error — the one mapper that had not been given the null guard its siblings carry. `TryPublishAsync` guarded only on the job still being Running with a matching worker, which is exactly the state a duplicate delivery arrives in, so a second delivery overwrote an already-succeeded child, contradicting the rule that completed children are never mutated.

The backtesting harness had a worse problem than being incomplete: its rider model was an empty band list, and `PowerLookup.GetWatts` short-circuits to a single global figure when a model has no bands. Every fixture rode a flat 200 W on a 9% climb and a -5% descent alike, and gradient-dependent power, band interpolation, and confidence blending were never exercised by any backtest.

A code review then found a lock-order inversion introduced by this work: baseline deletion had come to take `predictions` before `analysis_jobs`, while baseline publish, child publish, and child delete all take their job row first. A worker publishing during a delete could deadlock. The pre-change code carried a comment stating the jobs-first invariant, which a later reorder dropped, and task 15's own spec prescribes the inverted order.

## Decision

Deliver task 14 as specified across its nine files. Alignment happens once in C#, so the selected readout and the comparison charts work from one dictionary rather than joining in a render loop; a mismatch keeps the baseline map, charts, and readout and runs no comparison interop. Downsampling keeps the ends and both sides of every zone or phase change, keeping all mandatory points even past the display cap, because a semantic boundary is worth more than the cap.

Fix what the task 15 verification exposed rather than only recording it: null and null-entried collections become field errors, and a terminal child rejects republication.

Rewrite the rollout document against what shipped, stating at the top that it replaced its own contents so the superseded design is not mistaken for a live option, and record the seven-stage enable and rollback order, the operational signals with units and stable diagnostic codes, and an explicit split of what may and may not be logged.

Restore the jobs-first lock order in baseline deletion and name the global order in a comment. Task 15's prescribed order is wrong here and should be corrected in the plan.

Push the low-power failure mode down into `RoutePredictor` rather than leaving each strategy to guess. `TryAdvance` already floors the speed it derives driving force from, so below that speed the model has no opinion; a segment whose substep is collapsing is now carried at that floor and reported at Low confidence instead of failing the route. The iteration limit still fails, because a segment too long to simulate at one-second substeps is a different problem. The predictor warning is translated into an adjustment warning once at the publication boundary so every strategy surfaces it.

Rejected: making `BoundedPacingSearch` the place to fix the short-route convergence bug by tightening its default — the default was removed instead, because a dimensionless tolerance on a general-purpose routine is the trap. Also rejected: removing the per-strategy power floors once the predictor floor existed — 5 W is not a meaningful reading of "the bottom of zone 1", and substituting a rideable target is better than honouring the request at walking pace for fourteen hours.

## Consequences

An adjustment is now visible: baseline and adjustment power and speed as two lines, with deltas, segment-time deltas, and zone, phase, and W-prime annotations. Backtesting is five strategies across five route shapes against a model that varies with the terrain, and the documented fixture table is asserted by tests so evidence and harness cannot drift.

Two behaviours changed that were not merely untested: a duplicate delivery now returns false, and a request omitting a collection now returns 400. Anything depending on republishing over a succeeded child stops working, which is the intent.

Deliberately left open. The historical evidence rows in `backtesting.md` stay unfilled and marked "Not yet run" — fabricating them as CI would be worse than their absence. The matrix's `flat-long` row asks for both 120 x 100 m and over 30 minutes, which is unreachable at plausible power; `mountainous` carries the duration-band coverage instead and the deviation is recorded. The three configured limits (`MaximumDefinitionBytes`, `MaximumRules`, `MaximumPhases`) remain advertised metadata that must be kept in step with the domain constants by hand, and `MaximumPhases` has no domain counterpart at all.

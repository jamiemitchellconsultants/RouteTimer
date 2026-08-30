---
date: 2026-08-30
slug: complete-pacing-rollout-evidence-and-backtesting
title: "docs: complete pacing rollout evidence and backtesting"
summary: "Rewrite the rollout plan against the delivered append-only architecture and make the backtesting harness exercise a dense power model."
kind: product
status: accepted
sequence: 2026-08-30T20:10:00.000Z
evidence: "Branch feature/pacing-strategy-adjustments-tasks-09-16-17416437996458993256; follows [[implement-adjustment-visualization-and-lifecycle-hardening]]"
---

## Context

Task 16 had produced its two new files but neither was finished, and the file it was meant to correct was untouched. `06-cross-cutting-rollout.md` still described the design that the accepted append-only correction replaced: a strategy carried on prediction submission, a `POST /api/predictions/paced` endpoint, an `IncludeBaseline` flag, a `prediction_adjusted_segments` table, and strategy columns on `predictions`. An operator following it would have looked for endpoints and columns that do not exist.

The backtesting harness had a worse problem than being incomplete. Its rider model was `new PowerModel([], 200)` - an empty band list. `PowerLookup.GetWatts` short-circuits to a single global figure when a model has no bands, so every fixture rode at a flat 200 W on a 9% climb and a -5% descent alike, every segment was flagged extrapolated, and every route's confidence collapsed to Low. Gradient-dependent power, band interpolation, and confidence blending were never exercised by any backtest, on any fixture.

## Decision

Rewrite the rollout document against what shipped, and say plainly at the top that it replaced its own earlier contents so the superseded design is not mistaken for a live option. Record the seven-stage enable and rollback order, the operational signals with their units and stable diagnostic codes, and an explicit split of what may and may not be logged - the principle being that an operator needs to know which adjustment behaved how, never what the rider asked for.

Give the fixtures a dense 40-cell grid where watts rise with gradient and fall with elapsed duration, and add a test asserting the grid is dense and behaves that way, so the harness cannot quietly degenerate again. Add the invariants the plan named but the harness did not check: rerun determinism compared field-by-field, an unchanged baseline, a report that canonicalizes without NaN or Infinity, stable algorithm versions, both search evaluation caps, match-burning's two-replay refinement cap, and the direction cases for each strategy.

The matrix's `flat-long` row asked for both 120 x 100 m and a duration over 30 minutes. Those are not simultaneously reachable: 12 km in over 30 minutes needs an average under 6.7 m/s, which no plausible typical power produces for this rider. The purpose of that row was to reach past the model's first duration band, so `mountainous` carries it instead - its segment count and profile are as specified and its segment length, which the matrix does not constrain, is set to 250 m. The deviation is recorded in `backtesting.md` rather than resolved by quietly weakening either constraint.

## Consequences

The rollout document can be followed. The backtesting matrix is 5 strategies x 5 route shapes against a model that actually varies with the terrain, and the documented fixture table is asserted by tests, so evidence and harness cannot drift apart.

Two things this deliberately does not do. The historical gates stay unfilled rows marked "Not yet run" - fabricating them as CI would be worse than their absence, so the evidence table names a reviewer and a commit and waits. And the checkpoint's own `git diff --exit-code main...HEAD -- Narrative.md narrative/entries` gate now fails, because this branch adds narrative entries: the task asked for those files to stay untouched, while the repository's standing instruction is to record each session that changes behaviour. The standing instruction won; this entry is part of why the gate trips.

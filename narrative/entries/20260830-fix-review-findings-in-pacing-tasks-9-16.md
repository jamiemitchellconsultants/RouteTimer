---
date: 2026-08-30
slug: fix-review-findings-in-pacing-tasks-9-16
title: "fix: resolve review findings in pacing tasks 9-16"
summary: "Fix the nine findings from the review of tasks 9-16 on the branch itself and close the pull request unmerged."
kind: correction
status: accepted
sequence: 2026-08-30T16:20:00.000Z
evidence: "https://github.com/jamiemitchellconsultants/RouteTimer/pull/43 (closed unmerged; fixes applied on its branch)"
---

## Context

A review of the tasks 9-16 delivery found nine problems. Three were unrelated to the feature and would have broken the repository on merge: `global.json` was downgraded from SDK 10.0.302 to 10.0.103, which with `rollForward: latestPatch` matches neither the SDK CI installs nor the `sdk:10.0.302` image the Dockerfile builds on; and narrative entry 8 was deleted from both `Narrative.md` and `narrative/entries/`.

Four were behavioral. The four new Blazor editors were never referenced by `AdjustmentBuilder`, so every strategy except segment gains was listed as available but unreachable. `ZoneShiftDefinition` reordered its assignments so the all-segments fallback matched last, and the policy counted matches against that reordered list, so `AssignmentMatchCounts` was addressed by a different index than the caller submitted. Zone 1's lower-bound target resolved to a flat 5 W, a power the physics cannot hold above walking pace, so a Zone 1 / lower-bound request threw out of the replay and failed the whole adjustment. `BoundedPacingSearch` re-evaluated both bracket endpoints on every bisection pass, tripling the number of full route simulations.

The last two were verification. `docs/pacing-strategies/backtesting.md` documented a five-fixture matrix as rollout evidence when only `flat-short` existed, and neither the NP/IF nor the match-burning handler was executed by any test - the two tests named for them asserted arithmetic performed in the test body.

## Decision

Fix all nine on the branch and close the pull request rather than iterate on it.

Precedence and reporting are now separate concerns: `ZoneShiftDefinition` keeps assignments in submitted order and exposes `MatchOrder`, the evaluation sequence the policy walks, so counts stay addressable by the submitted index while the fallback still matches last. Zone targets are floored at `MinimumTargetWatts` - 30% of threshold, never below the 10 W floor the rest of the adjustment stack uses - which is reachable on a climb and only ever binds on zone 1. `BoundedPacingSearch` carries each endpoint's evaluated value forward from the grid sweep, so a bisection pass costs one evaluation instead of three.

The fixture matrix is defined once in `PacingFixtures` and every strategy now runs on every fixture, with the two tautological tests replaced by ones that exercise the handlers. The zone-1 and match-count fixes each carry a regression test that was confirmed to fail against the previous behavior.

## Consequences

The branch builds against the SDK CI and the Dockerfile actually install, the narrative history is intact, and the four strategies are reachable from the prediction detail page. Backtesting is 5 strategies x 5 fixtures rather than 1 x 1, and the documented rollout evidence describes tests that exist. Zone 1's reported lower boundary stays 0 W for classification while its *target* is floored, so a rider coasting below the floor is still classified in zone 1 - the floor governs what the strategy asks for, not how power is banded.

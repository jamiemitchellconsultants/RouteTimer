# Refined Tasks 9–16: Pacing Strategy Adjustments

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans`. Execute one
> numbered checkpoint at a time. Do not combine checkpoints, and do not begin the next checkpoint
> until the current checkpoint's focused tests pass and its diff has been reviewed.

**Goal:** Complete Tasks 9–16 of the approved pacing-strategy-adjustments design with enough exact
interfaces, tests, wiring instructions, and acceptance criteria for a local 27B model to implement
one checkpoint at a time without inventing architecture.

**Architecture:** Tasks 1–8 already provide the immutable baseline, adjustment aggregate, nested API,
generic dispatcher/job pipeline, client shell, and the segment-gains reference strategy. Tasks 9–13
add shared search/NP primitives and four strategy vertical slices; Task 14 overlays exactly one
selected adjustment; Tasks 15–16 harden and verify the complete system.

**Tech Stack:** .NET 10, C# 14, ASP.NET Core Minimal APIs, EF Core/PostgreSQL, Blazor WebAssembly,
System.Text.Json, xUnit, bUnit, Node's built-in test runner, Chart.js, and Leaflet.

**Spec:** `docs/superpowers/specs/2026-08-27-pacing-strategy-adjustments-design.md`

**Required history:** Read `../SUMMARY-tasks-01-08.md` before starting. It records implementation
facts that supersede assumptions in the original task files.

## Global constraints

- Preserve `POST /api/predictions`, all baseline response contracts, baseline exports, and history.
- Re-run the complete sequential `IRoutePredictor` simulation for every search candidate.
- Use the baseline's captured `PredictionRoute`, `RiderProfile`, and `RiderModel`; never load current
  rider state from inside a strategy.
- Strategy definitions contain exactly one strategy and serialize through their own handler.
- Contracts stays dependency-free; request-to-domain mappers live in `RouteTimer.Api/Adjustments`.
- Handler warnings contain only values from `AdjustmentWarningCodes`; never forward baseline
  `PredictionWarningCodes` from a recomputed `PredictionResult`.
- Strategy JSON is at most 65,536 UTF-8 bytes. Rule/window/assignment collections contain at most ten
  items. Constructors repeat authoritative validation so persisted JSON is revalidated in workers.
- Parent and per-strategy feature flags remain `false` in `src/RouteTimer.Api/appsettings.json`.
- Adjusted GPX/Garmin export, strategy composition, medical advice, and current-model recalculation are
  out of scope.
- Do not edit `Narrative.md` directly. None of these refined plan files changes an accepted decision.

## Current extension points—do not rediscover or replace these

- `IPacingStrategyHandler` owns `Canonicalize`, `Deserialize`, `CanonicalizeReport`, and `Run` for one
  concrete strategy.
- `PacingStrategyDispatcher` is already generic. Register each new handler in
  `src/RouteTimer.Api/Program.cs`; do not add strategy switch statements to the dispatcher or worker.
- `PredictionAdjustmentEndpoints.MapDefinition` is the only remaining domain-mapping switch. Give
  each strategy a mapper under `src/RouteTimer.Api/Adjustments/`, following
  `SegmentGainsRequestMapper` and returning indexed `ValidationProblem` errors where appropriate.
- `AdjustmentBuilder.razor` renders enabled editors. `AdjustmentComparison.razor` receives reports as
  opaque `JsonElement`; add a strategy-keyed rendering block there, not a client polymorphic hierarchy.
- `PredictionAdjustmentJobHandler.BuildPublication` already validates sequence parity, route totals,
  adjustment warnings, report JSON, averages, and optional segment annotations.
- `PacingStrategyJson` already canonicalizes concrete definitions/reports and checks definition bytes.
- `IRoutePredictor.Predict(PredictionRoute, RiderProfile, RiderModel, IPowerTargetPolicy?,
  CancellationToken)` is the only simulation seam.

## Execution protocol for a local model

For every checkpoint:

1. Read only this README, `../SUMMARY-tasks-01-08.md`, the current task file, and the production/test
   files listed by that checkpoint.
2. Run `git status --short`. Preserve all pre-existing user changes and stop if an intended edit
   overlaps an unexplained change.
3. Add the named failing tests. Run the exact focused command and confirm at least one named test fails
   for the expected missing behavior—not because the test project does not compile for an unrelated
   reason.
4. Implement only enough production code to satisfy that checkpoint.
5. Re-run the focused command, then `git diff --check`.
6. Review the diff against the checkpoint's “Do not” and acceptance lists.
7. Commit with the checkpoint's commit message. Push only when the task file explicitly reaches a push
   checkpoint.

If an exact type or member in the task conflicts with code added by an earlier refined task, preserve
the earlier public interface and update the later task documentation before implementing. Do not create
a second near-duplicate abstraction.

## Task index

9. [Bounded full-simulation search and normalized power](09-bounded-search-and-np.md)
10. [Time-target pacing](10-time-target-pacing.md)
11. [NP/IF targeting](11-np-if-targeting.md)
12. [FTP and inferred zone targeting](12-ftp-zone-targeting.md)
13. [Variable match-burning](13-variable-match-burning.md)
14. [One-adjustment visualization overlays](14-visualization-overlays.md)
15. [Lifecycle, limits, and backward compatibility](15-harden-lifecycle-and-limits.md)
16. [Rollout evidence and full verification](16-rollout-evidence-and-verification.md)

## Review checkpoints

- After Task 11: review the complete search family and NP calculation.
- After Task 13: review capacity inference, phase precedence, exponential W-prime recovery, and the
  one-pass refinement cap.
- After Task 16: run `superpowers:requesting-code-review`, then
  `superpowers:verification-before-completion` with fresh full-suite output.

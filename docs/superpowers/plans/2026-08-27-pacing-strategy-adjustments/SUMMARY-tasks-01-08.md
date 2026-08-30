[← Back to plan overview](README.md)

# Tasks 1–8 summary

Covers implementation of the pacing-strategy-adjustments plan from [Task 1](01-introduce-prediction-route.md)
through [Task 8](08-segment-specific-gains.md) — the plan's first "complete vertical slice" checkpoint.
Each task's own file has the full step-by-step record and its own "Implementation notes (deviations
from plan)" section; this document pulls out what actually broke, why, and how it was fixed, plus the
design decisions that shaped every task after them.

## Cross-cutting pattern: deferred concrete types

No concrete `PacingStrategyDefinition`/`PacingStrategyReport` subtype existed before Task 8, because
each strategy's own domain type is delivered in its own task (8, 10–13). Tasks 3 through 7 had to design
every intervening layer — contracts, JSON canonicalization, the dispatcher, the job handler, the API
endpoint, the client shell — to work generically against the abstract union, with no strategy yet able
to exercise the "happy path" end to end. This is the single biggest source of "deviation from plan" notes
across Tasks 3–7: several plan steps describe behavior (a working `MapDefinition` arm, a real strategy
editor, a genuine 202 response) that could not exist until Task 8 delivered the first concrete strategy.
Task 8 confirmed the design worked by using it for real, with no changes needed to any of the generic
layers themselves.

## Task-by-task: issues found and fixes

### Task 1 — Introduce `PredictionRoute`

- **Record equality quirk.** `PredictionResult`'s generated equality does reference comparison on its
  `Segments`/`Warnings` collections (pre-existing behavior, not introduced by this task), so two
  independently-built results never compare equal as a whole via `Assert.Equal`. Fixed by asserting
  field-by-field (`Confidence`, `Warnings`, `MovingTime`, `Segments`) instead. This same fix was needed
  again for `SegmentGainsDefinition.Rules` in Task 8.
- **Process issue, not a code bug:** the task's commit landed on `docs/split-pacing-adjustments-plan`,
  which had already been merged via a prior PR. Fixed by cherry-picking onto a fresh
  `feature/pacing-strategy-adjustments` branch cut from `origin/main` and deleting the stale local branch.

### Task 2 — Power-target-policy seam

No issues found. Added `PowerTargetContext`/`IPowerTargetPolicy` and threaded an optional
`IPowerTargetPolicy?` through `RoutePredictor.Predict` — this seam turned out to be exactly what
Task 8 needed to replay a strategy through real physics rather than approximating speed/time by hand.

### Task 3 — Adjustment domain & contracts

No bugs; the main decision was scope management — concrete definition/report subtypes deferred to each
strategy's own task, contracts kept self-contained (Contracts has zero project references), and
`PacingStrategyJson.Canonicalize<T>`/`Deserialize<T>` made generic over caller-supplied concrete types
so nothing downstream needed a concrete type to exist yet.

### Task 4 — Persist adjustment aggregates

- **`StrategyAlgorithmVersion` timing bug**, caught by design review before it shipped: the field was
  designed as required at *creation* time, but it is actually the handler's computed output, only known
  at *publish* time (`PacingStrategyComputation.AlgorithmVersion`). Fixed by making the persisted column
  nullable, moving it out of `QueuedAdjustmentCreation` and into `AdjustmentPublication`, and setting it
  in `TryPublishAsync` instead of `CreateQueuedAsync` — which required regenerating the EF migration.
- **jsonb byte-exactness assumption.** A test asserted `ResultJson` equal to a literal string, but
  Postgres jsonb columns reformat whitespace on storage, so the round-tripped value is never byte-for-byte
  identical. Fixed with `JsonElement.DeepEquals(...)` instead of a string comparison.

### Task 5 — Adjustment job orchestration

- **Step-ordering bug**, caught by a test with an empty dispatcher: `PredictionAdjustmentJobHandler`
  resolved the strategy handler (and deserialized the strategy) *before* mapping the persisted baseline
  segments to a `PredictionRoute`. A malformed-baseline test expected `invalid-prediction-adjustment-result`
  but got `InvalidOperationException: No pacing strategy handler registered...` instead, because handler
  resolution ran first and failed for the wrong reason. Fixed by reordering to match the design's literal
  numbered steps (map baseline first, dispatch handler second).
- Confirmed `AnalysisWorker` needed no changes — its dispatch is already fully generic by `job.Type`.

### Task 6 — Nested APIs and capabilities

Three related JSON-serialization bugs, all around the polymorphic `PacingStrategyRequest` discriminator:

- **`NotSupportedException` vs `JsonException`.** System.Text.Json throws `NotSupportedException`, not
  `JsonException`, when a polymorphic root's discriminator is missing or unrecognized. The endpoint's
  original `catch (JsonException)` let this leak as an unhandled 500. Fixed by widening the catch to
  `exception is JsonException or NotSupportedException`.
- **Type-inference footgun in `PostAsJsonAsync`/`JsonContent.Create`.** Both infer the JSON shape from the
  *compile-time* type of the argument, not its runtime type — passing a concrete subtype through an
  `object`-typed (or same-concrete-typed) parameter silently omits the discriminator, producing a request
  the server can't route. Caught by both an endpoint test and a dedicated client test asserting the wire
  body actually contains `"type":"time-target"`. Fixed by (a) using explicit generic calls in tests
  (`PostAsJsonAsync<PacingStrategyRequest>`) and (b) making `RouteTimerApiClient.CreatePredictionAdjustmentAsync`
  build its own `JsonContent.Create(request, options: ...)` with `request` declared as
  `PacingStrategyRequest`, not `object`.
- **Wrong assumed discriminator casing.** A test assumed camelCase (`"timeTarget"`), but the actual wire
  value is the literal string from `[JsonDerivedType]` (`"time-target"`) — camelCase only governs property
  names, not discriminator literals. Fixed the test assertion.

### Task 7 — Baseline-primary adjustment shell

- **bUnit selector ambiguity.** `[data-testid^=adjustment-card-]` matched both the card container and the
  nested `adjustment-card-state-{id}` element, since the latter's testid also starts with the former's
  prefix — a test expecting 2 elements got 4. Fixed by querying `div.prediction-detail-grid` instead of a
  testid prefix. The same class of bug was pre-empted in Task 8's client tests by asserting on
  `div.prediction-detail-grid` from the start.
- **Broke 5 pre-existing tests.** `PredictionDetail.razor` now unconditionally calls two new adjustment
  API methods for any succeeded baseline, and the fake API client throws `NotSupportedException` for any
  unconfigured method. Fixed by adding default `On...` configurations (disabled capabilities, empty list)
  to the shared test constructor.
- One flaky, unrelated Postgres Testcontainers test during a full-suite run, confirmed as a container
  startup flake (not a regression) by re-running that test class alone.

### Task 8 — Segment-specific pacing gains

No production bugs — the first strategy to go through the generic pipeline validated Tasks 3–7's design
decisions without requiring changes to any of them. Two minor authoring mistakes, both caught immediately
by the compiler/test run rather than surviving to review:

- A `JsonElement? report` narrowed via `is { } report` pattern-matches to the *unwrapped* `JsonElement`,
  not `JsonElement?` — an initial `report.Value.GetProperty(...)` in `AdjustmentComparison.razor` didn't
  compile; fixed by dropping `.Value`.
- The plan's own documented test filter (`FullyQualifiedName~SegmentSpecificGains`) is case-sensitive and
  matches on contiguous substrings, so initial test method names using `segment_specific_gains` (snake
  case) matched zero tests. Renamed the three affected methods to include the literal `SegmentSpecificGains`
  substring.

One deliberate design decision, not a bug: the adjusted route is recomputed by replaying the *entire*
baseline through the real `IRoutePredictor` with a custom `IPowerTargetPolicy` (the seam built in Task 2),
rather than hand-computing speed/time from the changed power per segment — this is what makes the result
physically consistent with the baseline, and is expected to be the pattern every remaining strategy (9–13)
follows.

## Status at Task 8

All five layers (domain, service, persistence, API, client) have a real, tested, working implementation
for one strategy end to end. Full solution test suite: 1,226 tests passing (Domain 37, Services 459 incl.
23 new for segment gains, Client 248 incl. 6 new, Api 306 incl. 3 new, Persistence 176), no regressions
introduced by Task 8. This is the plan's checkpoint after Task 8 ("first complete vertical slice") —
remaining work is Tasks 9–16: bounded search/NP-IF targeting, time-target pacing, RPE/zone shift, variable
match-burning, visualization overlays, lifecycle/limit hardening, and final rollout verification.

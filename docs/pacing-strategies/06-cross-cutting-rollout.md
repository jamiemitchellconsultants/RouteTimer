# Cross-Cutting Rollout Plan — All Five Strategies

**Date:** 2026-08-30
**Status:** Describes the delivered architecture and how to roll it out.

This document replaced its own earlier contents. The original plan proposed carrying a strategy on
prediction submission and storing one adjusted result beside the baseline. That design was rejected and
replaced by immutable baselines with append-only child adjustments — see the accepted narrative entries
`correct-pacing-strategies-to-append-only-adjustments` and
`docs-adopt-pacing-adjustment-architecture-and-implementation-plan`, and the per-strategy designs in
[`00-overview.md`](00-overview.md). Deterministic test evidence lives in
[`backtesting.md`](backtesting.md).

---

## What ships

A prediction is an immutable baseline. Once it succeeds, any number of child adjustments can be created
under it; each is computed by its own background job and is itself immutable once published. Deleting a
child never touches the baseline; deleting a baseline cascades its children and cancels their active jobs.

| Concern | Delivered shape |
|---|---|
| Create | `POST /api/predictions/{predictionId}/adjustments` — JSON body, polymorphic on `type`. Returns `202` with the adjustment and job ids. |
| Read | `GET /api/predictions/{predictionId}/adjustments` and `.../adjustments/{adjustmentId}` |
| Delete | `DELETE /api/predictions/{predictionId}/adjustments/{adjustmentId}` |
| Capabilities | `GET /api/pacing-strategies` — reports the flags and limits below |
| Storage | `prediction_adjustments` and `prediction_adjustment_segments`, added by the `AddPredictionAdjustments` migration. No strategy column is added to `predictions`. |
| Work | `AdjustPrediction` jobs on the existing analysis-job queue, owner-guarded at publication |

Baseline submission, its request and response contracts, and its exports are unchanged and carry no
adjustment-shaped field. A client that never enables pacing strategies sees no difference.

---

## Configuration

All of this is the `PacingStrategies` section. Every flag ships false.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Parent gate. False hides creation entirely. |
| `SegmentSpecificGains` | `false` | Per-strategy gate |
| `NpIfTarget` | `false` | Per-strategy gate |
| `TimeTarget` | `false` | Per-strategy gate |
| `RpeZoneShift` | `false` | Per-strategy gate |
| `VariableMatchBurning` | `false` | Per-strategy gate |
| `MaximumDefinitionBytes` | `65536` | Canonical strategy JSON limit, UTF-8 bytes |
| `MaximumRules` | `10` | Rules, assignments, or windows per definition |
| `MaximumPhases` | `10` | Phase entries per definition |

A strategy is creatable only when the parent flag and its own flag are both true. The flags are an
availability gate, not a validation boundary: server-side domain validation runs regardless.

---

## Stage order

Move one stage at a time, and only after the review named in the stage.

1. **Deploy migration and code with every `PacingStrategies` flag false.** The tables exist, no handler is
   reachable, and nothing changes for riders. Confirm the migration applied and baseline predictions still
   succeed.
2. **Enable `Enabled` and `SegmentSpecificGains` for internal riders.** The simplest strategy, and the one
   that validates the whole pipeline: creation, queueing, publication, read-back, deletion.
3. **Enable `TimeTarget` and `NpIfTarget`** after reviewing queue depth, handler runtime, and search
   evaluation counts from stage 2. Both run a bounded search, so they are the first strategies whose cost
   scales with route length rather than segment count alone.
4. **Enable `RpeZoneShift`** after reviewing threshold provenance in published reports — specifically how
   often `ModelInferred` is used instead of a supplied FTP, and the resulting zone distributions.
5. **Enable `VariableMatchBurning` last**, after reviewing W′ balance traces and the fatigue verdicts from
   a manual pass over stage-4 data. It is the only strategy that infers physiological capacity when the
   rider does not supply it.
6. **Roll back one strategy by setting its child flag false.** New creation stops with
   `409 pacing-strategy-disabled`. Adjustments already stored under it remain readable and deletable, and
   any job already queued for it still completes — disabling blocks creation, it does not strand accepted
   work.
7. **Roll back all creation by setting `Enabled` false.** Baseline predictions and every stored child
   remain readable. No data is removed at any stage of rollback.

There is no stage that deletes adjustments. Rollback is a gate change only.

---

## Operational signals

| Signal | Unit | Why |
|---|---|---|
| Queued adjustment age | seconds | Children compete with baseline predictions for the same workers; a rising age is the first sign the queue is saturated. |
| Handler runtime by strategy and algorithm version | seconds | Cost differs by an order of magnitude across strategies; version it so a handler change is attributable. |
| Search evaluation count | count | Time-target and NP/IF run a bounded search capped at 40 route simulations. Counts pinned at the cap mean the search is not converging. |
| Cancellation and failure count by diagnostic code | count | Grouped by the stable codes below, never by message text. |
| Publication conflict count | count | A publish rejected for stale ownership. A non-zero rate is expected under lease expiry; a rising rate means leases are too short or workers are stalling. |

Stable diagnostic codes to group by — API: `pacing-strategy-disabled`, `pacing-strategy-invalid`,
`pacing-strategy-too-large`, `pacing-strategy-capacity-required`, `pacing-strategy-target-infeasible`,
`adjustment-not-found`, `adjustment-baseline-not-ready`. Worker: `adjustment-missing`, `baseline-missing`,
`baseline-not-ready`, `model-missing`, `invalid-rider-model`, `invalid-prediction-adjustment-strategy`,
`invalid-prediction-adjustment-result`, `prediction-adjustment-sequence-mismatch`.

Algorithm versions currently published: `segment-gains-v1`, `time-target-v1`, `np-if-target-v1`,
`zone-shift-v1`, `match-burning-v1`.

---

## What may and may not be logged

A pacing adjustment's inputs are rider data. Logs are operational, not diagnostic replays.

**May be logged:** adjustment id, prediction id, job id, strategy enum name, algorithm version, adjustment
state, diagnostic code, counts (segments, rules, evaluations, warnings), and durations.

**Must not be logged:** the strategy JSON or report JSON in whole or part; route coordinates, elevations,
or any segment payload; power-model bands or global typical watts; critical power, W′, or FTP values;
target values such as target moving seconds or target intensity factor; and rider names or file names.

The distinction is that an operator needs to know *which* adjustment behaved how, never *what the rider
asked for*. Reproducing a failure is a database read against the stored canonical JSON under the same
access controls as the rest of the rider's data — not a log search.

---

## Verification before each stage

Deterministic evidence for every strategy on five synthetic route shapes is described in
[`backtesting.md`](backtesting.md), along with the manual historical gates that CI deliberately does not
fabricate. Run the deterministic suite before any stage change; record the manual gates against the stage
that enables the strategy they cover.

# Pacing Strategy Backtesting Evidence

## 1. Purpose, and what CI does not claim

Two kinds of evidence support a pacing-strategy rollout, and they must not be confused.

**Deterministic CI** proves the strategies are *correct software*: sequence parity with the baseline,
bounded simulation, finite non-negative power and speed, positive segment durations, publishable reports,
known warning codes, and stable algorithm versions. It runs on synthetic fixtures on every commit and
needs no rider data.

**Manual historical evaluation** proves the strategies are *physiologically plausible*, and can only be
done against retained private rides. It is recorded in section 6 by a named reviewer. CI never simulates,
approximates, or asserts these gates, and no CI run should be read as evidence for them.

Rollout stages that depend on these gates are listed in
[`06-cross-cutting-rollout.md`](06-cross-cutting-rollout.md).

## 2. Synthetic fixture matrix

Fixtures are defined once in `tests/RouteTimer.Services.Tests/Adjustments/PacingFixtures.cs` and driven by
`PacingStrategyBacktestingTests.cs`. They are built from `PredictionRoute`, `RiderProfile`, and
`RiderModel`; no rider GPS or power payload is stored.

| Fixture | Segments | Segment length | Profile |
|---|---:|---:|---|
| `flat-short` | 12 | 50 m | 0% throughout; baseline under 10 minutes |
| `flat-long` | 120 | 100 m | alternating -0.5% / +0.5% |
| `rolling` | 80 | 80 m | repeating -3%, 0%, +3%, +5% |
| `mountainous` | 100 | 250 m | sustained +6% / +9% blocks separated by -5% descents; crosses the 30-minute duration band |
| `fractional` | 31 | 37 m | 0%; segment durations do not land on whole seconds |

`The_fixture_matrix_matches_its_documented_shape` and
`The_fixture_matrix_covers_the_documented_gradient_and_duration_range` assert this table, so the document
cannot drift from the harness.

**Deviation from the original plan.** The task matrix asked for `flat-long` to exceed 30 minutes. At the
documented 120 x 100 m (12 km) that requires an average under 6.7 m/s, which no plausible typical power
produces for a 75 kg rider on a 10 kg bike — the fixture runs about 22 minutes. The purpose of that row
was to exercise the model's duration axis beyond its first band, so `mountainous` carries it instead: its
segment count and profile are as specified, and its segment length (unconstrained by the matrix) is set to
250 m so the ride crosses 30 minutes.

**Rider model.** A dense 40-cell grid over every `PowerModelBands` gradient x duration cell, with typical
watts rising with gradient and falling as elapsed duration grows, `PhysicalCoefficients.Default`,
conservative descent limits, rider 75 kg, bike and equipment 10 kg. The density matters:
`PowerLookup.GetWatts` short-circuits to a single global figure when a model has no bands, so a sparse
fixture would silently stop exercising gradient-dependent power, band interpolation, and confidence
blending. `The_fixture_model_is_a_dense_grid_whose_power_rises_with_gradient` pins this.

## 3. Deterministic invariants and tolerances

Every strategy runs on every fixture — 5 x 5 — and each run asserts:

| Invariant | Tolerance |
|---|---|
| Adjusted sequences equal baseline sequences, in order | exact |
| Power and speed finite and non-negative; segment duration positive | exact |
| Re-running the same definition reproduces the result field-by-field | exact |
| The baseline result is unchanged by the run | exact |
| The report canonicalizes without NaN or Infinity | exact |
| Warning codes are drawn from `AdjustmentWarningCodes` | exact |
| Algorithm version equals the handler's published constant | exact |
| Annotations key only real segment sequences | exact |

Field-level comparison is deliberate: these are collection-backed records, so record equality would
compare collections by reference and report a false difference between two identical runs.

Strategy-specific:

| Strategy | Assertion | Tolerance |
|---|---|---|
| Segment gains | Factor 1.10 on climbs raises power on every climb segment and does not increase total moving time | exact direction |
| Time target | Achieved time is no further from the target than the baseline was; evaluation count within budget | 40 evaluations |
| NP/IF | Achieved NP is no further from target than baseline NP; reported IF equals NP / FTP; a non-converged search warns `np-if-closest-feasible` | 1e-6 on IF; 40 evaluations |
| Zone shift | Every segment annotated; zone distribution totals 100%; per-assignment match counts follow submitted order | 1e-6 on percentages |
| Match burning | W' balance stays within capacity and never rises across a segment ridden above CP; burn phases equal the window's matched count; unmatched windows warn | exact direction |
| Match burning refinement | At most two route replays per run | 2 predictor calls |

Physiological accuracy is explicitly **not** asserted from synthetic fixtures.

## 4. Running the deterministic suite

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --filter "FullyQualifiedName~PacingStrategyBacktestingTests"
```

Test counts are recorded in the pull request for the run that produced them, not here — a count in a
document goes stale on the next commit that adds a case.

## 5. Manual historical protocol

Run before the rollout stage that enables the strategy under review.

1. Select retained rides from the reviewer's own account or an account whose owner has given explicit
   consent for this use. Do not use another rider's data to validate a feature.
2. Work in a local workspace. Nothing from this protocol is committed.
3. Identify each ride by a **dataset class** — for example `hilly-60-90min`, `flat-sub-60min` — never by
   rider name, ride title, date-plus-location, file name, or start coordinates. The class is what makes a
   result interpretable; the identity adds nothing and cannot be un-shared.
4. Record only the aggregate metric named in the gate, its threshold, and pass or fail. Do not paste
   report JSON, per-segment tables, or route geometry into the record.
5. Enter the outcome in section 6 with the reviewer's name and the commit reviewed.

Gates:

| Gate | Threshold |
|---|---|
| Time target achieved duration versus target on retained historical routes | within 5% |
| NP/IF target time recovery | within 3% |
| Zone distributions reviewed as representative of the ride | reviewer judgement, recorded |
| Match-burning climb speed versus expected capacity model, plus qualitative fatigue review | within 5%, plus recorded judgement |

## 6. Evidence table

Historical rows marked "Not yet run" are a statement about this document only. CI does not read this
table, and an unfilled row must never be reported as a passing gate.

| Date | Commit | Dataset class | Strategy / version | Samples | Metric | Threshold | Result | Reviewer |
|---|---|---|---|---:|---|---|---|---|
| — | — | — | `time-target-v1` | — | achieved vs target duration | within 5% | Not yet run | — |
| — | — | — | `np-if-target-v1` | — | time recovery | within 3% | Not yet run | — |
| — | — | — | `zone-shift-v1` | — | zone distribution representativeness | reviewer judgement | Not yet run | — |
| — | — | — | `match-burning-v1` | — | climb speed vs capacity model | within 5% | Not yet run | — |
| — | — | — | `match-burning-v1` | — | qualitative fatigue review | reviewer judgement | Not yet run | — |

## 7. Data handling

Never commit, attach to an issue, or paste into a review:

- GPX or FIT files, or any excerpt of one;
- rider model JSON, power-model bands, or global typical watts;
- exact route coordinates, elevation series, or per-segment tables;
- rider names, account identifiers, ride titles, or source file names;
- full strategy or report JSON;
- critical power, W', FTP, or target values taken from a real rider.

What may be recorded: the dataset class, sample count, the aggregate metric and its threshold, the
strategy and algorithm version, the commit, and the reviewer. The same split applies to application logs —
see the logging rules in [`06-cross-cutting-rollout.md`](06-cross-cutting-rollout.md).

Deterministic CI is designed so that none of the above is ever needed to reproduce a failure: every
fixture is constructed in code.

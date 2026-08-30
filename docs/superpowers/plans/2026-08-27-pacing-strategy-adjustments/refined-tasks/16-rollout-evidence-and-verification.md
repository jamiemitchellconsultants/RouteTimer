[← Refined task index](README.md)

# Task 16: Add rollout evidence and complete system verification

**Deliverable:** Deterministic synthetic back-testing evidence, operator-facing staged rollout/rollback
instructions, full migration/test evidence, and a review-ready final diff. Private historical ride data is
not added to the repository and its absence does not make deterministic CI fail.

## Files

- Create: `tests/RouteTimer.Services.Tests/Adjustments/PacingStrategyBacktestingTests.cs`
- Create: `docs/pacing-strategies/backtesting.md`
- Modify: `docs/pacing-strategies/06-cross-cutting-rollout.md`
- Verify unchanged: `README.md`. It has no feature-flag/operator configuration section; do not add one
  for this feature. Link rollout documentation from `docs/pacing-strategies/backtesting.md` instead.
- Do not modify `Narrative.md` or accepted files under `narrative/entries/`.

## Deterministic fixture matrix

Build fixtures in C# using `PredictionRoute`, `PredictionResult`, captured `RiderProfile`, and
`RiderModel`; do not store rider GPS/power payloads. Use these exact shapes:

| Fixture | Segments | Required characteristic |
| --- | ---: | --- |
| flat-short | 12 | 50 m each, 0% grade, baseline under 10 minutes |
| flat-long | 120 | 100 m each, alternating -0.5%/+0.5%, over 30 minutes |
| rolling | 80 | repeating -3%, 0%, +3%, +5% |
| mountainous | 100 | sustained +6%/+9% blocks separated by descents |
| fractional | 31 | predictor fixture producing at least one fractional-second duration |

Use a dense deterministic power model derived from `PowerModelBands` with higher typical watts in steeper
and shorter-duration bands, `PhysicalCoefficients.Default`, conservative descent limits from existing test
fixtures, rider 75 kg, bike/equipment 10 kg. Construct the immutable baseline with the real
`RoutePredictor` and use the same captured context for all handlers.

## Back-testing assertions

Create one test per matrix row plus strategy-specific theories. Every handler run asserts:

- finite non-negative power/speed and positive duration for every segment;
- exact sequence parity and deterministic rerun equality field-by-field (do not compare collection-backed
  records as a whole; Tasks 1/8 documented reference equality);
- unchanged baseline fields/collections after the run;
- finite report JSON and known adjustment warnings;
- strategy algorithm version is non-empty/stable;
- Task 10/11 evaluation count at most 40; Task 13 predictor calls at most two.

Use these direction/semantic cases:

- segment gains factor 1.10 on climbs: climb power rises and total time does not increase;
- time target 5% faster than baseline: achieved result moves toward target versus baseline; converge or
  report closest/infeasible without exceeding cap;
- NP/IF target above baseline IF: achieved NP moves toward target and reported IF equals NP/FTP;
- zone shift all-segments higher zone: annotation exists for every sequence and percentages total 100%;
- match burning one mountain climb window: burn phase matches selector, W-prime does not increase during
  above-CP burn, annotations cover every sequence, refinement cap holds.

Do not require physiological accuracy from synthetic fixtures. Historical/private gates are recorded as
manual rollout evidence only:

- time target within 5% on retained historical routes;
- NP/IF time recovery within 3%;
- representative zone distributions reviewed against rides;
- match-burning climb speed within 5% plus qualitative fatigue review.

## `docs/pacing-strategies/backtesting.md` contents

Write these sections with actual test names/commands—no empty result tables:

1. Purpose and distinction between deterministic CI and private historical evaluation.
2. Synthetic fixture matrix with units and construction source.
3. Per-strategy deterministic invariants and tolerances.
4. Command to run back-testing alone.
5. Manual historical protocol, required anonymization, and the four gates above.
6. Evidence table columns: date, commit, dataset class (never rider name), strategy/version, sample count,
   metric, threshold, result, reviewer. Mark historical rows “Not yet run” only in docs; CI does not parse
   them as passed.
7. Data handling: never commit GPX/FIT, model JSON, exact route coordinates, names, or full strategy JSON.

## Rollout/rollback document correction

Update `06-cross-cutting-rollout.md` so the approved append-only adjustment architecture supersedes its
old strategy-at-submission endpoint/schema examples. Do not leave examples of
`POST /api/predictions/paced`, `includeBaseline`, strategy columns on `predictions`, or one adjusted result.
Link to the approved design and backtesting page.

Document exact stage order:

1. deploy migration/code with all `PacingStrategies` flags false;
2. enable parent + `SegmentSpecificGains` for internal riders;
3. enable `TimeTarget` and `NpIfTarget` after queue/runtime/evaluation review;
4. enable `RpeZoneShift` after provenance/report review;
5. enable `VariableMatchBurning` last after W-prime/manual review;
6. rollback one strategy by disabling its child flag—new creation stops, stored children remain readable;
7. rollback all creation by disabling parent—baseline predictions and stored children remain readable.

List operational signals and units: queued adjustment age seconds; handler runtime seconds by strategy and
algorithm version; search evaluation count; cancellation/failure count by stable diagnostic; publication
conflict count. Logs may contain adjustment ID, strategy enum, algorithm version, counts, duration, and
diagnostic code. Logs must not contain strategy/report JSON, coordinates, model bands, CP/W-prime values,
FTP, target values, or route payloads.

## Checkpoint 16.1: Back-testing tests

- [ ] Add fixture builders and tests above. Reuse existing test utilities when they already expose the
  required deterministic types; do not copy production physics.
- [ ] Use field-level parity so collection-backed record reference equality cannot create a false failure:

```csharp
private static void AssertSequenceParity(PredictionResult baseline, PredictionResult adjusted)
{
    Assert.Equal(
        baseline.Segments.Select(segment => segment.Sequence),
        adjusted.Segments.Select(segment => segment.Sequence));
    Assert.Equal(baseline.Segments.Count, adjusted.Segments.Count);
    Assert.All(adjusted.Segments, segment =>
    {
        Assert.True(double.IsFinite(segment.PowerWatts));
        Assert.True(double.IsFinite(segment.SpeedMetresPerSecond));
        Assert.True(segment.MovingTime > TimeSpan.Zero);
    });
}
```

- [ ] Run the new class and confirm failure only for unmet invariants or missing strategy code:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~PacingStrategyBacktestingTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

- [ ] Fix only deterministic implementation defects exposed by the harness, rerun, and record the final
  focused command and invariant matrix in `backtesting.md`; do not hard-code an evergreen solution-wide
  test count there.
- [ ] Commit: `test: add pacing strategy backtesting fixtures`.

## Checkpoint 16.2: Rollout and data-handling docs

- [ ] Create `backtesting.md` and correct `06-cross-cutting-rollout.md` as specified.
- [ ] Search for stale architecture in the two documents:

```bash
rg -n "predictions/paced|includeBaseline|prediction_adjusted_segments|strategy columns on predictions" docs/pacing-strategies/06-cross-cutting-rollout.md docs/pacing-strategies/backtesting.md
```

Expected: no matches. The corrected document describes only nested append-only adjustments.

- [ ] Verify every production flag remains false and limits remain exact:

```bash
rg -n '"(Enabled|SegmentSpecificGains|NpIfTarget|TimeTarget|RpeZoneShift|VariableMatchBurning)": false|"MaximumDefinitionBytes": 65536|"MaximumRules": 10|"MaximumPhases": 10' src/RouteTimer.Api/appsettings.json
```

- [ ] Commit: `docs: add pacing adjustment rollout evidence`.

## Checkpoint 16.3: Narrative contract and generated-file safety

The original plan's hard-coded Narrative CLI under `/private/tmp` is not portable and that CLI is absent
in the current workspace. Use repository-owned evidence instead:

- [ ] Confirm the accepted correction exists and cites the superseded slug:

```bash
rg -n "slug: correct-pacing-strategies-to-append-only-adjustments|docs-add-pacing-strategy-implementation-plans" narrative/entries/20260827-correct-pacing-strategies-to-append-only-adjustments.md
```

- [ ] Confirm this branch did not hand-edit generated/history files:

```bash
git diff --exit-code main...HEAD -- Narrative.md narrative/entries
```

Expected: exit 0. If it does not, stop and review; do not rewrite an accepted entry. A genuine new/reversed
decision requires a new fragment and the PR's `narrative-required` label plus exact body headings
`## Narrative Context`, `## Narrative Decision`, and `## Narrative Consequences`. Documentation/evidence
that implements the already accepted decision should not manufacture another decision entry.

The repository's `.github/workflows/validate-narrative.yml` remains the authoritative compiler check for
any narrative-file change. Do not install or vendor an unpinned Narrative CLI in this task.

## Checkpoint 16.4: Full verification and final diff

- [ ] Run fresh, complete verification:

```bash
npm test --prefix src/RouteTimer.Client
dotnet test RouteTimer.slnx --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
git status --short
git diff --stat main...HEAD
```

- [ ] Record project-by-project test counts from this run in the PR description, not as hard-coded
  evergreen counts in documentation.
- [ ] Inspect migration/model snapshot consistency, baseline contract tests, flag defaults, absence of
  adjusted export controls, closed warnings, no secrets/payload logging, and service/API/client coverage
  for all five strategies.
- [ ] Run `superpowers:requesting-code-review`. Resolve findings and repeat the affected verification.
- [ ] Run `superpowers:verification-before-completion` and cite the fresh command output before claiming
  production readiness.
- [ ] Push the final commits:

```bash
git push
```

If a PR already exists and `gh` is authenticated, verify repository workflows after the push:

```bash
gh pr checks --watch
```

Do not create a PR solely to run this optional command; hand off the verified branch when no PR exists.

## Task 16 acceptance

- Deterministic CI covers five route shapes and all five strategies without private data.
- Historical physiological/accuracy gates are documented as manual evidence, never fabricated as CI.
- Rollout docs use append-only nested adjustments and exact flag behavior.
- Narrative history/generated files remain untouched unless a separately reviewed decision requires them.
- Fresh Node, full .NET solution, diff, and workflow evidence are available at the final review checkpoint.

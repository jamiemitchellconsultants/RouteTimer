[← Refined task index](README.md)

# Task 13: Deliver variable match-burning end to end

**Depends on:** Task 12's `PowerZoneResolver` for CP-anchored zone midpoint intensities.

**Risk rule:** Execute checkpoints 13.1–13.6 separately. This task contains four independently testable
algorithms; a single implementation pass is not reviewable and is likely to hide unit/provenance errors.

## Files

- Create: `src/RouteTimer.Domain/Adjustments/MatchBurning/MatchBurningDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/MatchBurning/MatchBurningReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/CapacityResolver.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/MatchPhasePlanner.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/WPrimeBalanceCalculator.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/MatchBurningPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/MatchBurningHandler.cs`
- Create: `src/RouteTimer.Api/Adjustments/MatchBurningRequestMapper.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/MatchBurningEditor.razor`
- Create: `tests/RouteTimer.Services.Tests/Adjustments/MatchBurning/CapacityResolverTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Adjustments/MatchBurning/MatchPhasePlannerTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Adjustments/MatchBurning/WPrimeBalanceCalculatorTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Adjustments/MatchBurning/MatchBurningHandlerTests.cs`
- Create: `tests/RouteTimer.Client.Tests/MatchBurningEditorTests.cs`
- Modify: `src/RouteTimer.Domain/Adjustments/AdjustmentWarningCodes.cs`
- Modify: `src/RouteTimer.Api/Endpoints/PredictionAdjustmentEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Client/Components/Adjustments/AdjustmentBuilder.razor`
- Modify: `src/RouteTimer.Client/Components/Adjustments/AdjustmentComparison.razor`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionAdjustmentShellTests.cs`

No migration is required: Task 4 already added `StrategyPhase` and `WPrimeBalanceJoules` annotation
columns plus opaque report JSON.

## Domain definitions

Create these types under `RouteTimer.Domain.Adjustments.MatchBurning`:

```csharp
public enum MatchBurnSelector { Gradient, Distance, Sequence }
public enum MatchBurnIntensity { AbsoluteWatts, PercentCp, CpZone }
public enum MatchPhase { Baseline, Conservation, Recovery, Burn }
public enum CapacityProvenance { Supplied, InferredModel, Fallback }
public enum MatchBurningVerdict { Manageable, Aggressive, Risky, Infeasible }

public sealed record MatchBurnWindow
{
    public MatchBurnWindow(
        double? minGradient, double? maxGradient,
        double? minDistanceMetres, double? maxDistanceMetres,
        int? minSequence, int? maxSequence,
        double? absoluteWatts, double? percentCp, int? cpZone);

    public MatchBurnSelector Selector { get; }
    public MatchBurnIntensity Intensity { get; }
    public double? MinGradient { get; }
    public double? MaxGradient { get; }
    public double? MinDistanceMetres { get; }
    public double? MaxDistanceMetres { get; }
    public int? MinSequence { get; }
    public int? MaxSequence { get; }
    public double? AbsoluteWatts { get; }
    public double? PercentCp { get; }
    public int? CpZone { get; }
    public bool Matches(PredictionRouteSegment segment);
}

public sealed record MatchBurningDefinition : PacingStrategyDefinition
{
    public const int MaximumWindows = 10;
    public MatchBurningDefinition(
        double? criticalPowerWatts,
        double? wPrimeJoules,
        IReadOnlyList<MatchBurnWindow> windows,
        double conservationDurationSeconds,
        double conservationTargetCpFraction,
        double recoveryDurationSeconds,
        double recoveryTargetCpFraction,
        bool includeFatigueReport,
        bool enableRefinement);

    public double? CriticalPowerWatts { get; }
    public double? WPrimeJoules { get; }
    public IReadOnlyList<MatchBurnWindow> Windows { get; }
    public double ConservationDurationSeconds { get; }
    public double ConservationTargetCpFraction { get; }
    public double RecoveryDurationSeconds { get; }
    public double RecoveryTargetCpFraction { get; }
    public bool IncludeFatigueReport { get; }
    public bool EnableRefinement { get; }
}
```

Validation is exact:

- optional CP is finite `[1,2000]`; optional W-prime is finite `[1000,100000]`;
- one to ten non-null windows are required;
- a window selects exactly one bound family and at least one bound in that family; bounds are finite,
  non-negative for distance, and ordered; matching is inclusive;
- a window supplies exactly one intensity; absolute watts `[10,2000]`, percent CP `[0.5,3.0]`, or CP
  zone 1–7;
- conservation duration is finite `[0,300]`, target fraction `[0.5,1.0]`;
- recovery duration is finite `[0,600]`, target fraction `[0.5,0.9]`.

CP-zone intensity means the midpoint from Task 12's seven FTP-style bands with CP passed as the
threshold: Zones 1–6 use the closed midpoint and Zone 7 uses `1.60 * CP`. It does not use the rider's
FTP and does not add the Zone 7 upper-cap warning.

## Report shapes

```csharp
public sealed record MatchBurnWindowReport(int WindowIndex, int MatchedSegmentCount);
public sealed record MatchPhaseReport(MatchPhase Phase, int SegmentCount, double MovingSeconds);

public sealed record MatchBurningReport(
    double CriticalPowerWatts,
    CapacityProvenance CriticalPowerProvenance,
    double WPrimeJoules,
    CapacityProvenance WPrimeProvenance,
    IReadOnlyList<MatchBurnWindowReport> Windows,
    IReadOnlyList<MatchPhaseReport> Phases,
    double MinimumWPrimeBalanceJoules,
    double FinalWPrimeBalanceJoules,
    double DepletedFraction,
    double TimeAboveCriticalPowerSeconds,
    double WorkAboveCriticalPowerJoules,
    IReadOnlyList<int> CriticalSequences,
    int? FirstInfeasibleSequence,
    MatchBurningVerdict Verdict,
    bool RefinementEnabled,
    bool RefinementRan,
    bool RefinementChangedAssignments,
    double MovingTimeDeltaSeconds,
    double AverageSpeedDeltaMetresPerSecond,
    double AveragePowerDeltaWatts)
    : PacingStrategyReport(PacingStrategyType.VariableMatchBurning);
```

When `IncludeFatigueReport` is false, still compute W-prime for verdict/warnings/annotations, but return
empty `CriticalSequences`; the remaining aggregate fields stay populated so summaries are explainable.

Add these missing closed-catalog warnings and include them in `AdjustmentWarningCodes.All`:

```csharp
public const string MatchBurningOverlappingWindows = "match-burning-overlapping-windows";
public const string MatchBurningWindowNoMatch = "match-burning-window-no-match";
```

Existing match-burning codes keep their meanings:

- `match-burning-cp-low-confidence`: CP used global fallback rather than the exact long-duration band;
- `match-burning-wprime-inferred-default`: W-prime used 15,000 J fallback;
- `match-burning-reserve-breach`: adjusted W-prime balance fell below 20% of starting capacity.

## Capacity resolution

Create:

```csharp
public sealed record ResolvedMatchCapacity(
    double CriticalPowerWatts,
    CapacityProvenance CriticalPowerProvenance,
    double WPrimeJoules,
    CapacityProvenance WPrimeProvenance,
    IReadOnlyList<string> Warnings);

public static class CapacityResolver
{
    public static ResolvedMatchCapacity Resolve(MatchBurningDefinition definition, PowerModel model);
}
```

Resolve CP and W-prime independently:

1. A supplied value wins and has `Supplied` provenance.
2. Missing CP uses `0.95 * TypicalWatts` from the exact `("-1:1", "180:+")` band when positive and
   finite; provenance `InferredModel`.
3. Without that evidence, use `0.95 * model.GlobalTypicalWatts`; provenance `Fallback`, add
   `match-burning-cp-low-confidence`, and throw if the result is non-positive/non-finite.
4. Missing W-prime uses `(TypicalWatts("1:3", "0:30") - resolved CP) * 900`. If the evidence exists and
   result is positive/finite, clamp it into `[1000,100000]` and use `InferredModel`.
5. Otherwise use exactly 15,000 J, provenance `Fallback`, and add
   `match-burning-wprime-inferred-default`.

## Phase planning

Create:

```csharp
public sealed record MatchPhaseAssignment(int Sequence, MatchPhase Phase, int? BurnWindowIndex);

public sealed record MatchPhasePlan(
    IReadOnlyDictionary<int, MatchPhaseAssignment> BySequence,
    IReadOnlyList<int> WindowMatchCounts,
    bool HasOverlappingBurnWindows);

public static class MatchPhasePlanner
{
    public static MatchPhasePlan Plan(
        PredictionRoute route,
        PredictionResult timing,
        MatchBurningDefinition definition);
}
```

Require route and timing sequence equality. Determine burn membership first. If multiple windows match,
the lowest request index supplies intensity and `HasOverlappingBurnWindows = true`. Consecutive burn
segments form a burn block even if their source windows differ.

For each burn block, walk backward from its first segment, adding whole baseline-timed segments until
the configured conservation duration is met or the route starts. Walk forward similarly for recovery.
Zero duration assigns no phase. Final precedence is `Burn > Recovery > Conservation > Baseline`; when a
non-burn segment is both recovery and conservation, Recovery wins. Every route sequence gets exactly one
assignment. Window match counts count raw selector matches, not winning matches.

## W-prime calculation

Create:

```csharp
public sealed record WPrimeBalancePoint(
    int Sequence,
    double DisplayBalanceJoules,
    bool Infeasible);

public sealed record WPrimeBalanceResult(
    IReadOnlyList<WPrimeBalancePoint> Points,
    double MinimumBalanceJoules,
    double FinalBalanceJoules,
    double TimeAboveCriticalPowerSeconds,
    double WorkAboveCriticalPowerJoules,
    int? FirstInfeasibleSequence,
    MatchBurningVerdict Verdict);

public static class WPrimeBalanceCalculator
{
    public static WPrimeBalanceResult Calculate(
        IReadOnlyList<PredictionSegment> segments,
        double criticalPowerWatts,
        double wPrimeJoules);
}
```

Validate finite positive CP/W-prime and ordered segments with finite non-negative power and positive
duration. Start full. For each constant-power segment:

```text
if P > CP:
    raw balance -= (P - CP) * duration
else if P < CP:
    DCP = CP - P
    tau = 546 * exp(-0.01 * DCP) + 316
    raw balance = Wprime - (Wprime - raw balance) * exp(-duration / tau)
else:
    raw balance unchanged
```

Clamp recovery to at most starting W-prime. At the first `raw balance <= 0`, set
`FirstInfeasibleSequence`; that and all later points have display
balance zero and `Infeasible = true` even if later below-CP work would mathematically recover. Continue
walking segments to preserve time/work totals. `MinimumBalanceJoules` and `FinalBalanceJoules` are display
balances and therefore stay in `[0, WPrimeJoules]`; the raw negative value is an internal crossing test
and is never serialized.

Verdict uses minimum display balance divided by starting W-prime: `>=0.30` Manageable, `>=0.10`
Aggressive, `>0` Risky, otherwise Infeasible.

## Checkpoint 13.1: Domain and capacity

- [ ] Constructor tests cover every numeric boundary, exactly-one selector/intensity, inclusive matches,
  one/ten/eleven windows, and invalid enums.
- [ ] Capacity tests cover supplied/supplied, exact-band inference, independent supplied/inferred values,
  global CP fallback warning, positive W-prime inference/clamp, and 15,000 J fallback warning.
- [ ] Implement domain records, report records, capacity resolver, and the two warning constants.
- [ ] Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~CapacityResolver|FullyQualifiedName~MatchBurningDefinition" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

- [ ] Commit: `feat: add match burning capacity model`.

## Checkpoint 13.2: Deterministic phase planner

- [ ] Tests cover each selector boundary, first matching window intensity index, overlapping-window flag,
  contiguous burn union, whole-segment conservation/recovery duration, route edges, zero durations,
  `Burn > Recovery > Conservation`, recovery-vs-conservation overlap, sequence mismatch rejection, and
  raw per-window match counts.
- [ ] Use short fixtures whose segment durations are visibly different; include expected assignment maps
  such as `[(1, Conservation), (2, Burn), (3, Recovery), (4, Baseline)]`.
- [ ] Implement the pure planner. It must not call the predictor or mutate the definition.
- [ ] Run the `MatchPhasePlanner` filter and commit: `feat: add match burning phase planner`.

## Checkpoint 13.3: Exponential W-prime balance

- [ ] Add tests for exact CP, above-CP linear expenditure, below-CP exponential recovery from a partially
  depleted balance, zero/large duration validation, full-capacity ceiling, first zero crossing, sticky
  displayed infeasibility, time/work totals, all four verdict boundaries, and non-finite rejection.
- [ ] Include this numerical fixture: CP 250 W, W-prime 20,000 J, 60 seconds at 350 W spends exactly
  6,000 J and leaves 14,000 J before any recovery.

```csharp
[Fact]
public void Calculate_spends_work_above_cp_linearly()
{
    var segment = new PredictionSegment(
        1, 500, .04, 350, 8, TimeSpan.FromSeconds(60), ConfidenceLevel.High);

    var result = WPrimeBalanceCalculator.Calculate([segment], 250, 20_000);

    Assert.Equal(14_000, Assert.Single(result.Points).DisplayBalanceJoules, 9);
    Assert.Equal(6_000, result.WorkAboveCriticalPowerJoules, 9);
    Assert.Null(result.FirstInfeasibleSequence);
}
```

- [ ] Implement the exact exponential equation; do not introduce a configurable/fixed linear recovery
  rate.
- [ ] Run the `WPrimeBalanceCalculator` filter and commit: `feat: add exponential w prime balance`.

## Checkpoint 13.4: Policy, handler, and one-pass refinement

`MatchBurningPolicy.Resolve` uses the precomputed assignment:

- Burn: selected window absolute watts, percent × CP, or CP-zone midpoint.
- Conservation/recovery: configured fraction × CP.
- Baseline: unchanged `BaselineEstimate`.
- Always preserve baseline estimate metadata with a `with` expression.

Handler flow is fixed:

1. resolve capacity;
2. plan using immutable baseline durations;
3. run one full prediction;
4. if `EnableRefinement`, plan again using first adjusted durations;
5. compare `(Sequence, Phase, BurnWindowIndex)`; only when changed, run exactly one second prediction;
6. never plan or predict a third time;
7. calculate W-prime from final adjusted segments;
8. build annotations for every sequence using lowercase phase and displayed W-prime balance;
9. assemble report/warnings and return `AlgorithmVersion = "match-burning-v1"`.

Use these exact service constructors:

```csharp
public sealed class MatchBurningPolicy(
    MatchBurningDefinition definition,
    ResolvedMatchCapacity capacity,
    MatchPhasePlan plan,
    ResolvedPowerZoneSet cpAnchoredZones) : IPowerTargetPolicy
{
    public PowerEstimate Resolve(PowerTargetContext context);
}

public sealed class MatchBurningHandler(IRoutePredictor routePredictor) : IPacingStrategyHandler
{
    public const string AlgorithmVersion = "match-burning-v1";
    public PacingStrategyType Type => PacingStrategyType.VariableMatchBurning;
    public string Canonicalize(PacingStrategyDefinition strategy) =>
        PacingStrategyJson.Canonicalize((MatchBurningDefinition)strategy);
    public PacingStrategyDefinition Deserialize(string canonicalJson) =>
        PacingStrategyJson.Deserialize<MatchBurningDefinition>(canonicalJson);
    public string CanonicalizeReport(PacingStrategyReport report) =>
        PacingStrategyJson.CanonicalizeReport((MatchBurningReport)report);
}
```

`MatchBurningHandler.Run(PacingStrategyContext, PacingStrategyDefinition, CancellationToken)` follows
the fixed nine-step flow above and returns `PacingStrategyComputation`.

Add overlap warning when the final plan reports overlap, unmatched warning once when any window count is
zero, and reserve warning when any display balance is below 20% of capacity. Add capacity warnings without
duplicates and in this stable order: CP, W-prime, overlap, unmatched, reserve.

- [ ] Handler tests cover all four policy phases, absolute/percent/zone intensities, full-physics replay,
  no-refinement single run, enabled-but-unchanged single run, changed-plan exactly two runs, hard two-run
  cap, annotations, warnings/order, report aggregates, cancellation, canonical round-trip, and version.
- [ ] Implement policy and handler. Do not register it yet.
- [ ] Run all MatchBurning Services tests and commit: `feat: add match burning simulation`.

## Checkpoint 13.5: API mapping and worker integration

- [ ] Create mapper parsing exact ordinal selector literals `gradient`, `distance`, `sequence` and
  intensity literals `absolute-watts`, `percent-cp`, `cp-zone`. The wire fields `Selector` and `Intensity` must
  agree with the non-null bound/value family; return row errors keyed `windows[N]`.
- [ ] Map `VariableMatchBurningRequest` to the exact domain constructor, replace its
  `NotImplementedException`, add validation catch, and register `MatchBurningHandler` in `Program.cs`.
- [ ] API tests cover valid `202`, each malformed union, zero/eleven windows, CP/W-prime/range limits,
  disabled `409`, and serialized discriminator `"type":"variable-match-burning"`.
- [ ] Add one workflow test using the real handler and fake repository proving phase/W-prime annotations
  enter `AdjustmentPublication`; assert baseline segments remain unchanged.
- [ ] Run focused Services/API tests and commit: `feat: wire variable match burning adjustments`.

## Checkpoint 13.6: Editor and fatigue report

- [ ] Editor defaults: infer CP/W-prime, one gradient burn window, percent-CP `1.20`, conservation 120 s
  at 0.80 CP, recovery 300 s at 0.70 CP, fatigue report on, refinement off.
- [ ] IDs use `match-burning-`: capacity toggle/CP/W-prime, add-window, indexed selector/min/max/intensity/
  value/remove/error, conservation duration/fraction, recovery duration/fraction, fatigue, refinement,
  submit, and error. Prevent an eleventh row; switching selector/intensity clears stale hidden fields.
- [ ] Submit the exact existing contract shape using the selector/intensity wire strings above. Render
  indexed server errors and callback ID.
- [ ] Report rendering shows capacity values/provenance, explicit “estimate—not a physiological
  measurement” wording, phase counts/time, minimum/final balance, depleted percent, critical sequences,
  verdict, and whether refinement actually reran. Never recommend effort/training actions.
- [ ] Add builder capability and bUnit tests for defaults, toggles, row limits, stale-field clearing,
  request shape, errors, callback, capability, provenance, and infeasible report.
- [ ] Run:

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~MatchBurning|FullyQualifiedName~PredictionAdjustmentShell" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~MatchBurning|FullyQualifiedName~WPrime|FullyQualifiedName~CapacityResolver" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~MatchBurning" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] Commit and push:

```bash
git add src tests
git commit -m "feat: add variable match burning UI"
git push
```

## Task 13 acceptance

- Capacity provenance and warnings distinguish supplied, inferred-model, and fallback values.
- Burn windows—not user-authored phases—are the request primitive.
- Phase priority and first-window intensity are deterministic.
- Recovery is exponential, zero depletion is sticky for displayed feasibility, and simulation continues.
- Refinement is triggered only by changed phase membership and never exceeds two predictor runs.
- Every annotation uses an existing adjusted sequence and a closed warning code.

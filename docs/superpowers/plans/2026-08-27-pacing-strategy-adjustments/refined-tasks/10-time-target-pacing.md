[← Refined task index](README.md)

# Task 10: Deliver time-target pacing end to end

**Depends on:** Task 9's `BoundedPacingSearch` interface exactly as written in this refined plan.

**Deliverable:** A complete `time-target` vertical slice: validated domain definition, request mapper,
full-simulation handler, feature-flagged API creation, editor, and typed report rendering. No schema or
contract-record change is required because Task 3 already shipped `TimeTargetRequest` and generic report
JSON.

## Files

- Create: `src/RouteTimer.Domain/Adjustments/TimeTarget/TimeTargetDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/TimeTarget/TimeTargetReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/TimeTarget/TimeTargetPowerPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/TimeTarget/TimeTargetHandler.cs`
- Create: `src/RouteTimer.Api/Adjustments/TimeTargetRequestMapper.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/TimeTargetEditor.razor`
- Create: `tests/RouteTimer.Services.Tests/Adjustments/TimeTarget/TimeTargetHandlerTests.cs`
- Create: `tests/RouteTimer.Client.Tests/TimeTargetEditorTests.cs`
- Modify: `src/RouteTimer.Api/Endpoints/PredictionAdjustmentEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Client/Components/Adjustments/AdjustmentBuilder.razor`
- Modify: `src/RouteTimer.Client/Components/Adjustments/AdjustmentComparison.razor`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionAdjustmentShellTests.cs`

Do not modify `PacingStrategyContracts.cs`, `PredictionAdjustmentContracts.cs`, persistence entities,
migrations, `PacingStrategyDispatcher`, or `PredictionAdjustmentJobHandler`.

## Domain interfaces and constants

Create these exact public shapes in namespace `RouteTimer.Domain.Adjustments.TimeTarget`:

```csharp
public enum TimeTargetDistribution { Proportional, ClimbFocused }
public enum TimeTargetFeasibilityVerdict { Achievable, Challenging, Extreme, Impossible }

public sealed record TimeTargetDefinition : PacingStrategyDefinition
{
    public const double MinimumTargetSeconds = 1;
    public const double MaximumTargetSeconds = 172800;

    public TimeTargetDefinition(
        double targetMovingSeconds,
        TimeTargetDistribution distribution,
        double? climbBias,
        bool includeFeasibilityReport);

    public double TargetMovingSeconds { get; }
    public TimeTargetDistribution Distribution { get; }
    public double? ClimbBias { get; }
    public bool IncludeFeasibilityReport { get; }
}

public sealed record TimeTargetGradientBandReport(
    string GradientBand,
    double MovingSeconds,
    double BaselineEstimateWattSeconds,
    double RequiredWattSeconds,
    double DemandRatio);

public sealed record TimeTargetReport(
    double TargetMovingSeconds,
    double AchievedMovingSeconds,
    double AbsoluteMissSeconds,
    double PercentageMiss,
    TimeTargetDistribution Distribution,
    double SelectedOuterScale,
    double SelectedClimbScale,
    double SelectedOtherScale,
    bool Converged,
    bool Bracketed,
    int EvaluationCount,
    double? FastestBoundSeconds,
    double? SlowestBoundSeconds,
    IReadOnlyList<TimeTargetGradientBandReport> GradientBands,
    TimeTargetFeasibilityVerdict Verdict,
    double MovingTimeDeltaSeconds,
    double AverageSpeedDeltaMetresPerSecond,
    double AveragePowerDeltaWatts)
    : PacingStrategyReport(PacingStrategyType.TimeTarget);
```

Constructor validation is authoritative and must survive JSON deserialization:

- target seconds is finite and in `[1, 172800]`;
- `Distribution` is defined;
- `Proportional` requires `climbBias == null`;
- `ClimbFocused` requires finite `climbBias` in `[1.0, 2.0]`;
- no `EvenEffort` enum value or alias exists.

The handler constants are:

```csharp
public sealed class TimeTargetPowerPolicy(
    double climbScale,
    double otherScale) : IPowerTargetPolicy
{
    public PowerEstimate Resolve(PowerTargetContext context);
}

public sealed class TimeTargetHandler(IRoutePredictor routePredictor) : IPacingStrategyHandler
{
    public PacingStrategyType Type => PacingStrategyType.TimeTarget;
    public const string AlgorithmVersion = "time-target-v1";
    private static readonly PacingSearchOptions SearchOptions = new(0.3, 4.0, 9, 30.0, 40);

    public string Canonicalize(PacingStrategyDefinition strategy) =>
        PacingStrategyJson.Canonicalize((TimeTargetDefinition)strategy);
    public PacingStrategyDefinition Deserialize(string canonicalJson) =>
        PacingStrategyJson.Deserialize<TimeTargetDefinition>(canonicalJson);
    public string CanonicalizeReport(PacingStrategyReport report) =>
        PacingStrategyJson.CanonicalizeReport((TimeTargetReport)report);
}
```

`TimeTargetHandler.Run(PacingStrategyContext, PacingStrategyDefinition, CancellationToken)` implements
the algorithm below and returns `PacingStrategyComputation`.

## Algorithm

`TimeTargetPowerPolicy` receives `climbScale` and `otherScale`. The handler retains `outerScale` for the
report. The policy returns the baseline
estimate with watts multiplied by `climbScale` when `Gradient >= 0.03`, otherwise by `otherScale`.
Preserve confidence, extrapolation, and reason using
`context.BaselineEstimate with { Watts = context.BaselineEstimate.Watts * selectedScale }`. Never use
route-average power as a segment target.

Before search, compute climb fraction from the immutable baseline result:

```text
climb seconds = sum baseline segment moving seconds where matching route gradient >= 0.03
f = climb seconds / baseline moving seconds
normalizer = f * bias + (1 - f)
climbScale = outerScale * bias / normalizer
otherScale = outerScale / normalizer
```

For proportional mode, both scales equal `outerScale`. For climb-focused with `f == 0`, both equal
`outerScale` and add `time-target-no-climbs` once to the final computation.

Every evaluator call creates a new policy, invokes the real `IRoutePredictor`, and returns objective
`adjusted.MovingTime.TotalSeconds - TargetMovingSeconds`. Convert `PredictionCalculationException` or
non-finite output into failed evaluation code `time-target-candidate-invalid`; do not catch cancellation.
If every candidate is invalid, throw `ArgumentException("Time-target search produced no valid simulation candidate.")`.

For the report:

- selected search result supplies adjusted prediction/scales;
- `FastestBoundSeconds`/`SlowestBoundSeconds` are min/max moving seconds from valid lower/upper-bound
  evaluations only, otherwise null;
- absolute miss is `Math.Abs(achieved - target)` and percentage miss is absolute miss / target × 100;
- averages use the same duration-weighted formulas as `SegmentGainsHandler`;
- non-converged results add `time-target-infeasible` but still publish successfully.

When feasibility is requested, construct `PowerLookup(context.Model.PowerModel)`. Walk adjusted segments
in route order, querying the captured model with route gradient and the segment's adjusted start time.
Group by `PowerModelBands.FindGradientBand(routeSegment.Gradient).Key`. For each band, divide adjusted
power watt-seconds by baseline-estimate watt-seconds. A zero denominator with positive required work is
positive infinity and therefore Impossible. Worst finite ratio determines: `<=1.2` Achievable, `<=1.5`
Challenging, `<=2.0` Extreme, otherwise Impossible. A non-converged, non-bracketed search is Impossible.
When feasibility is not requested, return an empty band list but still derive the route verdict from
convergence (`Achievable` if converged, otherwise `Impossible`).

## Checkpoint 10.1: Domain validation and API mapping

- [ ] Add domain-constructor tests to `TimeTargetHandlerTests` for every boundary and invalid enum.
  Start with this exact red test:

```csharp
[Fact]
public void Definition_rejects_a_climb_bias_in_proportional_mode()
{
    var exception = Assert.Throws<ArgumentException>(() =>
        new TimeTargetDefinition(3600, TimeTargetDistribution.Proportional, 1.2, true));

    Assert.Contains("climb bias", exception.Message, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] Implement the definition/report records.
- [ ] Create `TimeTargetRequestMapper.ToDefinition(TimeTargetRequest)` and a
  `TimeTargetRequestValidationException` carrying `IReadOnlyDictionary<string,string[]> Errors`.
  Parse exact ordinal wire literals `proportional` and `climb-focused`; reject every other string under
  field key `distribution`. Map constructor failures to `targetMovingSeconds` or
  `climbBias` according to the violated field.
- [ ] Replace the current `TimeTargetRequest` arm that throws `NotImplementedException` in
  `PredictionAdjustmentEndpoints.MapDefinition` and catch the mapper exception beside the existing
  segment-gains validation catch.
- [ ] Add API tests proving valid JSON returns `202`, `EvenEffort` returns `400`, proportional+bias
  returns `400`, climb-focused without bias returns `400`, and the disabled flag remains `409`.
- [ ] Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~TimeTarget" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~TimeTarget" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

- [ ] Commit: `feat: add time target domain and mapping`.

## Checkpoint 10.2: Policy, search, and report

- [ ] Add tests named `Run_uses_proportional_full_simulation`,
  `Run_normalizes_climb_focused_scales_from_baseline_time`, `Run_falls_back_when_route_has_no_climb`,
  `Run_handles_faster_slower_and_equal_targets`, `Run_publishes_closest_non_converged_candidate`,
  `Run_never_exceeds_forty_simulations`, `Run_recovers_from_one_invalid_candidate`,
  `Run_throws_when_all_candidates_are_invalid`, `Run_checks_cancellation_between_simulations`, and
  `Run_classifies_all_feasibility_thresholds`.
- [ ] Pin the normalization formula independently of the predictor:

```csharp
[Fact]
public void Climb_focused_scales_preserve_the_requested_weighted_mean()
{
    const double climbFraction = 0.25;
    const double outerScale = 1.4;
    const double bias = 1.8;
    var normalizer = (climbFraction * bias) + (1 - climbFraction);
    var climb = outerScale * bias / normalizer;
    var other = outerScale / normalizer;

    Assert.Equal(outerScale, (climbFraction * climb) + ((1 - climbFraction) * other), 12);
}
```

- [ ] Use a counting `IRoutePredictor` fake for cap/failure tests and the real `RoutePredictor` with
  `PredictionFixtures` for physics-direction tests. Do not assert exact physics outputs from a fake.
- [ ] Implement policy and handler. Register `TimeTargetHandler` as
  `IPacingStrategyHandler` in `Program.cs` without enabling its flag in configuration.
- [ ] Add a canonicalization round-trip test and assert `AlgorithmVersion == "time-target-v1"`.
- [ ] Run the focused Services and API commands above. Commit: `feat: add time target simulation`.

## Checkpoint 10.3: Editor and report rendering

- [ ] Create an editor with test IDs `time-target-duration`, `time-target-distribution`,
  `time-target-climb-bias`, `time-target-feasibility`, `time-target-submit`, and `time-target-error`.
- [ ] Accept exactly `hh:mm:ss`, including hours 24–48. Do not use `TimeSpan.TryParseExact` with `hh`
  because it treats hours as a day component and cannot represent the full product range. Use this helper;
  reject blank, malformed, below one second, or above 48 hours before calling the API:

```csharp
private static bool TryParseDuration(string? value, out double totalSeconds)
{
    totalSeconds = 0;
    var parts = value?.Split(':');
    if (parts is not { Length: 3 } || parts.Any(part => part.Length != 2) ||
        !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
        !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
        !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
        hours is < 0 or > 48 || minutes is < 0 or > 59 || seconds is < 0 or > 59)
    {
        return false;
    }

    totalSeconds = (hours * 3600d) + (minutes * 60d) + seconds;
    return totalSeconds is >= TimeTargetDefinition.MinimumTargetSeconds
        and <= TimeTargetDefinition.MaximumTargetSeconds;
}
```

  Render a specific inline error and keep submit disabled while invalid. Test `23:59:59`, `24:00:00`,
  `48:00:00`, `48:00:01`, bad minute/second values, and missing zero-padded fields.
- [ ] Hide/clear climb bias in proportional mode. Default to proportional, `01:00:00`, no bias, and
  feasibility checked.
- [ ] Submit wire distribution `proportional` or `climb-focused`; canonical domain/report JSON remains
  camelCase because `PacingStrategyJson` serializes domain enums independently of request strings.
- [ ] Add the editor under `Capabilities.TimeTarget` in `AdjustmentBuilder.razor`.
- [ ] In `AdjustmentComparison.razor`, when `StrategyType == "TimeTarget"`, render target, achieved,
  faster/slower miss, convergence, verdict with “model estimate” wording, and gradient bands only when
  present. Read exact camelCase properties from `JsonElement`.
- [ ] bUnit tests cover parsing, mode switching, request shape, API errors, callback ID, and report
  rendering. Extend the shell test to prove the editor appears only when its capability is true.
- [ ] Run:

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~TimeTarget|FullyQualifiedName~PredictionAdjustmentShell" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] Commit and push:

```bash
git add src tests
git commit -m "feat: add time target pacing UI"
git push
```

## Task 10 acceptance

- At most 40 full simulations occur and all search bounds/tolerances are literal constants above.
- Climb normalization uses baseline moving time, not segment count or adjusted time.
- Infeasible is a successful stored adjustment with a warning; all-invalid remains a failed job.
- API mapping lives in API, flags remain off, and the client describes feasibility as model output.

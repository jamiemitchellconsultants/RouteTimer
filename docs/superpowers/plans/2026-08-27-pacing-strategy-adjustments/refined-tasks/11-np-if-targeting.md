[← Refined task index](README.md)

# Task 11: Deliver normalized-power/intensity-factor targeting end to end

**Depends on:** Task 9. Task 10 is not a code dependency, but its mapper/editor/report patterns are the
reference for this vertical slice.

## Files

- Create: `src/RouteTimer.Domain/Adjustments/NpIf/NpIfTargetDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/NpIf/NpIfTargetReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/NpIf/NpIfPowerPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/NpIf/NpIfTargetHandler.cs`
- Create: `src/RouteTimer.Api/Adjustments/NpIfRequestMapper.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/NpIfTargetEditor.razor`
- Create: `tests/RouteTimer.Services.Tests/Adjustments/NpIf/NpIfTargetHandlerTests.cs`
- Create: `tests/RouteTimer.Client.Tests/NpIfTargetEditorTests.cs`
- Modify: `src/RouteTimer.Api/Endpoints/PredictionAdjustmentEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Client/Components/Adjustments/AdjustmentBuilder.razor`
- Modify: `src/RouteTimer.Client/Components/Adjustments/AdjustmentComparison.razor`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionAdjustmentShellTests.cs`

## Exact types

Namespace `RouteTimer.Domain.Adjustments.NpIf`:

```csharp
public enum NpIfScalingMode { Proportional, Additive }

public sealed record NpIfTargetDefinition : PacingStrategyDefinition
{
    public NpIfTargetDefinition(double targetIntensityFactor, double ftpWatts, NpIfScalingMode mode);
    public double TargetIntensityFactor { get; }
    public double FtpWatts { get; }
    public NpIfScalingMode Mode { get; }
}

public sealed record NpIfTargetReport(
    double TargetNormalizedPowerWatts,
    double AchievedNormalizedPowerWatts,
    double TargetIntensityFactor,
    double AchievedIntensityFactor,
    double FtpWatts,
    NpIfScalingMode Mode,
    double SelectedParameter,
    bool Converged,
    bool Bracketed,
    int EvaluationCount,
    bool UsedShortRouteApproximation,
    double MovingTimeDeltaSeconds,
    double AverageSpeedDeltaMetresPerSecond,
    double AveragePowerDeltaWatts)
    : PacingStrategyReport(PacingStrategyType.NpIfTarget);
```

Validation: target IF is finite in `(0, 1.5]`; FTP is finite in `[1, 2000]`; mode is defined.

Handler constants:

```csharp
public sealed class NpIfPowerPolicy(
    NpIfScalingMode mode,
    double parameter) : IPowerTargetPolicy
{
    public PowerEstimate Resolve(PowerTargetContext context);
}

public sealed class NpIfTargetHandler(IRoutePredictor routePredictor) : IPacingStrategyHandler
{
    public PacingStrategyType Type => PacingStrategyType.NpIfTarget;
    public const string AlgorithmVersion = "np-if-target-v1";
    private const int CoarseGridPointCount = 9;
    private const int MaximumEvaluations = 40;
    private const double ObjectiveToleranceWatts = 0.5;

    public string Canonicalize(PacingStrategyDefinition strategy) =>
        PacingStrategyJson.Canonicalize((NpIfTargetDefinition)strategy);
    public PacingStrategyDefinition Deserialize(string canonicalJson) =>
        PacingStrategyJson.Deserialize<NpIfTargetDefinition>(canonicalJson);
    public string CanonicalizeReport(PacingStrategyReport report) =>
        PacingStrategyJson.CanonicalizeReport((NpIfTargetReport)report);
}
```

`NpIfTargetHandler.Run(PacingStrategyContext, PacingStrategyDefinition, CancellationToken)` implements
the algorithm below and returns `PacingStrategyComputation`.

Proportional options are `(0.1, 5.0, 9, 0.5, 40)`; additive options are
`(-2000.0, 2000.0, 9, 0.5, 40)`.

## Algorithm and warnings

`NpIfPowerPolicy` preserves the baseline estimate except watts:

```csharp
var watts = mode == NpIfScalingMode.Proportional
    ? context.BaselineEstimate.Watts * parameter
    : context.BaselineEstimate.Watts + parameter;
return context.BaselineEstimate with { Watts = Math.Max(0, watts) };
```

Every candidate uses a fresh policy and complete `IRoutePredictor` call. Calculate NP from adjusted
segments using Task 9, then objective:

```csharp
normalizedPower.Watts - definition.FtpWatts * definition.TargetIntensityFactor
```

Convert candidate `PredictionCalculationException` to failure `np-if-candidate-invalid`; propagate
cancellation. All-invalid throws `ArgumentException("NP/IF search produced no valid simulation candidate.")`.
Non-converged closest-valid results publish and add `np-if-closest-feasible`.

Warnings are built once from the selected result:

- total route duration below 600 seconds: `np-if-short-route-approximation` (including the under-30
  mean-power fallback);
- proportional selected parameter `< 0.5`: `np-if-low-intensity`;
- proportional selected parameter `> 2.0`: `np-if-high-intensity`;
- non-converged: `np-if-closest-feasible`.

Do not apply low/high warnings to additive watts; the approved thresholds are multiplier thresholds.
Never forward recomputed baseline prediction warnings.

## Checkpoint 11.1: Domain and request mapper

- [ ] Write constructor tests for IF `0`, `1.5`, above `1.5`, FTP `1`, `2000`, non-finite values, and an
  invalid enum.

```csharp
[Theory]
[InlineData(0)]
[InlineData(1.500001)]
public void Definition_rejects_target_if_outside_the_closed_upper_range(double targetIf)
{
    Assert.Throws<ArgumentException>(() =>
        new NpIfTargetDefinition(targetIf, 300, NpIfScalingMode.Proportional));
}
```

- [ ] Implement records and `NpIfRequestMapper.ToDefinition(NpIfTargetRequest)`. Accept exact ordinal
  mode strings `proportional` and `additive`; indexed/list errors are not needed. Expose field keys
  `targetIntensityFactor`, `ftpWatts`, and `mode` through `NpIfRequestValidationException.Errors`.
- [ ] Replace only the NP/IF `NotImplementedException` arm and add endpoint catch/test coverage for
  valid `202`, invalid ranges `400`, unknown mode `400`, and disabled `409`.
- [ ] Run focused Services/API tests. Commit: `feat: add np if domain and mapping`.

## Checkpoint 11.2: Full-simulation target handler

- [ ] Add tests named `Run_proportional_mode_scales_baseline_estimates`,
  `Run_additive_mode_offsets_baseline_estimates`, `Run_uses_normalized_power_as_objective`,
  `Run_uses_mean_power_under_thirty_seconds`, `Run_warns_for_every_route_under_ten_minutes`,
  `Run_reports_exact_target_as_converged`, `Run_publishes_unreachable_high_and_low_targets`,
  `Run_recovers_from_invalid_candidate`, `Run_fails_when_every_candidate_is_invalid`,
  `Run_caps_simulations_at_forty`, `Run_propagates_cancellation`, and
  `Handler_round_trips_definition_and_report`.
- [ ] Assert the objective itself rather than only the selected parameter:

```csharp
const double ftp = 300;
const double targetIf = 0.8;
const double achievedNp = 247;
var objective = achievedNp - (ftp * targetIf);
Assert.Equal(7, objective, 12);
```

- [ ] For objective tests, inject a counting predictor whose result duration/power is a deterministic
  function of policy output. Separately use the real predictor to prove changed watts alter speed/time.
- [ ] Implement policy/handler and register the handler in `Program.cs`. Keep feature flags false.
- [ ] Report target NP as `FTP * target IF`, achieved IF as `achieved NP / FTP`, Task 9 evaluation count,
  selected parameter, Task 9 flags, NP fallback flag, and duration-weighted route deltas.
- [ ] Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~NpIf|FullyQualifiedName~NormalizedPowerCalculator|FullyQualifiedName~BoundedPacingSearch" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~NpIf" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

- [ ] Commit: `feat: add normalized power targeting`.

## Checkpoint 11.3: Editor, report, and search-family review

- [ ] Create inputs/test IDs `np-if-target`, `np-if-ftp`, `np-if-mode`, `np-if-submit`, and
  `np-if-error`. Defaults: IF `0.85`, FTP blank, proportional. Submit stays disabled until both numeric
  fields pass the domain ranges.
- [ ] Submit `NpIfTargetRequest` using exact mode strings `proportional`/`additive`. Explain: “FTP is used only for this adjustment;
  it does not change your rider model.”
- [ ] Render target/achieved NP and IF, mode, multiplier or additive watts, convergence, evaluation count,
  and route deltas in `AdjustmentComparison.razor` for `StrategyType == "NpIfTarget"`.
- [ ] Test client validation, mode switching, request fields, API errors, callback, capability gating, and
  both proportional/additive report labels.
- [ ] Run:

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~NpIf|FullyQualifiedName~PredictionAdjustmentShell" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~BoundedPacingSearch|FullyQualifiedName~NormalizedPowerCalculator|FullyQualifiedName~TimeTarget|FullyQualifiedName~NpIf" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] Review that Tasks 10/11 share Task 9 unchanged, both have exactly nine coarse points and a maximum
  of 40 simulations, and neither catches `OperationCanceledException`.
- [ ] Commit and push:

```bash
git add src tests
git commit -m "feat: add normalized power targeting UI"
git push
```

## Task 11 acceptance

- NP/IF uses adjusted durations, not baseline durations or post-hoc power scaling.
- Short-route and low/high/fallback warnings follow the exact conditions above.
- Search diagnostics remain in the report; all-invalid is a job failure, unreachable is successful.
- Task 9 interfaces are reused without a strategy-specific fork.

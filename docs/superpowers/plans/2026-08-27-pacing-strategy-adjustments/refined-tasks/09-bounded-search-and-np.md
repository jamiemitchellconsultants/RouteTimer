[← Refined task index](README.md)

# Task 9: Implement bounded full-simulation search and normalized-power primitives

**Deliverable:** Two synchronous, deterministic, strategy-neutral services. This task does not add a
strategy handler, API contract, feature flag, persistence change, or client UI.

## Files

- Create: `src/RouteTimer.Services/Adjustments/BoundedPacingSearch.cs`
- Create: `src/RouteTimer.Services/Adjustments/NormalizedPowerCalculator.cs`
- Create: `tests/RouteTimer.Services.Tests/Adjustments/BoundedPacingSearchTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Adjustments/NormalizedPowerCalculatorTests.cs`

## Public interfaces

Create these types in namespace `RouteTimer.Services.Adjustments`:

```csharp
public sealed record PacingSearchOptions(
    double LowerBound,
    double UpperBound,
    int CoarseGridPointCount,
    double ObjectiveTolerance,
    int MaximumEvaluations);

public sealed record PacingSearchEvaluation<T>(
    T? Value,
    double? Objective,
    string? FailureCode)
{
    public static PacingSearchEvaluation<T> Success(T value, double objective) =>
        new(value, objective, null);

    public static PacingSearchEvaluation<T> Failure(string failureCode) =>
        new(default, null, failureCode);
}

public sealed record PacingSearchCandidate<T>(
    double Parameter,
    T? Value,
    double? Objective,
    string? FailureCode)
{
    public bool IsValid => Value is not null && Objective is not null && FailureCode is null;
}

public sealed record PacingSearchResult<T>(
    PacingSearchCandidate<T>? Selected,
    IReadOnlyList<PacingSearchCandidate<T>> Evaluated,
    bool Converged,
    bool Bracketed);

public static class BoundedPacingSearch
{
    public static PacingSearchResult<T> Run<T>(
        PacingSearchOptions options,
        Func<double, PacingSearchEvaluation<T>> evaluate,
        CancellationToken cancellationToken)
        where T : class;
}

public sealed record NormalizedPowerResult(
    double Watts,
    bool UsedShortRouteApproximation);

public static class NormalizedPowerCalculator
{
    public static NormalizedPowerResult Calculate(
        IReadOnlyList<PredictionSegment> segments);
}
```

`Selected` is `null` only when every evaluation failed. Strategy handlers in Tasks 10 and 11 must turn
that outcome into an `ArgumentException` so the existing adjustment job boundary records
`invalid-prediction-adjustment-result`.

## Deterministic search rules

1. Reject non-finite bounds/tolerance, `LowerBound >= UpperBound`, `CoarseGridPointCount < 2`,
   `ObjectiveTolerance < 0`, or `MaximumEvaluations < CoarseGridPointCount` with `ArgumentException`.
2. Evaluate exactly `CoarseGridPointCount` evenly spaced values including both bounds. With nine points
   on `[0.1, 0.9]`, evaluate `0.1, 0.2, …, 0.9` in ascending order.
3. Check cancellation immediately before every evaluator invocation.
4. Convert an evaluation to a failed candidate when its objective is null/non-finite, its value is null,
   or it supplies a failure code. Preserve a supplied failure code; otherwise use the stable internal
   code `pacing-search-invalid-evaluation`. Do not let an invalid candidate enter bracket selection.
5. Never evaluate the same `double` parameter twice. Track parameters by exact `double` equality; all
   parameters are generated internally from bounds and midpoints.
6. A candidate converges when `Math.Abs(Objective.Value) <= ObjectiveTolerance`.
7. After the whole coarse grid is evaluated, find adjacent *grid positions* whose valid objectives have
   opposite signs. Invalid positions break adjacency; do not bracket across one.
8. If several brackets exist, choose the bracket whose better endpoint has the smallest absolute
   objective; tie by lower endpoint parameter.
9. Bisect the chosen bracket until a candidate converges, the midpoint duplicates an existing parameter,
   or `MaximumEvaluations` is reached. Replace the endpoint having the same objective sign as the new
   midpoint.
10. `Selected` is the valid candidate with smallest absolute objective across all evaluations. Ties use
    earlier evaluation order. `Converged` reflects `Selected`; `Bracketed` records whether a sign-changing
    bracket was found, even if the evaluation cap prevented convergence.
11. If no bracket exists, return the closest valid coarse candidate with `Converged = false` unless it was
    already within tolerance. If no valid candidate exists, return `Selected = null`.

## Normalized-power sampling rules

Treat each prediction segment as constant power over its positive `MovingTime` and concatenate segments
without gaps, ordered as supplied. Reject null segments, an empty list, duplicate/out-of-order sequence,
non-positive/non-finite duration, or negative/non-finite watts with `ArgumentException`.

- For total duration below 30 seconds, return duration-weighted mean power and
  `UsedShortRouteApproximation = true`.
- Otherwise divide elapsed time into intervals `[0,1)`, `[1,2)`, … plus a final fractional interval.
  A bucket's power is energy divided by actual covered bucket duration, so a segment boundary inside a
  bucket is energy-weighted correctly.
- At every completed bucket ending at or after 30 seconds, integrate the preceding 30 seconds of bucket
  power, weighting partial overlap at both ends, and divide by exactly 30 seconds.
- Raise each trailing mean to the fourth power. Weight that fourth-power sample by the duration of the
  newly completed bucket (one second except the final partial bucket), then take the fourth root of the
  weighted mean.
- A route of exactly 30 seconds has one trailing window. A route of 30.001 seconds has the 30-second
  window plus a 0.001-second-weighted final window. Constant power must return that power within `1e-9`.
- `UsedShortRouteApproximation` is `false` at 30 seconds and above. The separate under-ten-minute warning
  is a Task 11 handler concern.

## Checkpoint 9.1: Search validation and fixed grid

- [ ] Add tests named:
  `Run_rejects_invalid_options`, `Run_evaluates_the_fixed_grid_in_ascending_order`,
  `Run_records_invalid_candidates_without_aborting`, and `Run_checks_cancellation_before_evaluation`.

Use a reference object so `T : class` is satisfied:

```csharp
private sealed record SampleValue(double Parameter);

[Fact]
public void Run_evaluates_the_fixed_grid_in_ascending_order()
{
    var seen = new List<double>();
    var result = BoundedPacingSearch.Run(
        new PacingSearchOptions(0, 4, 5, 0.01, 10),
        parameter =>
        {
            seen.Add(parameter);
            return PacingSearchEvaluation<SampleValue>.Success(new(parameter), parameter - 9);
        },
        CancellationToken.None);

    Assert.Equal([0d, 1d, 2d, 3d, 4d], seen);
    Assert.Equal(4, result.Selected!.Parameter);
    Assert.False(result.Bracketed);
}
```

- [ ] Run and confirm failure because `BoundedPacingSearch` does not exist:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~BoundedPacingSearchTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

- [ ] Implement option validation, fixed-grid generation, candidate normalization, cancellation, and
  closest-valid selection. Do not implement bisection yet.
- [ ] Re-run the command. The four checkpoint tests must pass.
- [ ] Commit:

```bash
git add src/RouteTimer.Services/Adjustments/BoundedPacingSearch.cs tests/RouteTimer.Services.Tests/Adjustments/BoundedPacingSearchTests.cs
git commit -m "feat: add bounded pacing search grid"
```

## Checkpoint 9.2: Bracket selection and bisection

- [ ] Add tests named:
  `Run_selects_the_best_adjacent_sign_changing_bracket`, `Run_does_not_bracket_across_an_invalid_grid_point`,
  `Run_bisects_until_tolerance`, `Run_stops_at_the_evaluation_cap`,
  `Run_does_not_repeat_an_exact_bound_or_midpoint`, `Run_returns_an_exact_grid_hit`,
  `Run_ties_by_evaluation_order`, and `Run_returns_null_when_all_candidates_fail`.
- [ ] For bisection use `objective = parameter - 0.3`, bounds `[0, 1]`, three grid points, tolerance
  `0.0001`, and cap 20. Assert the selected parameter is within `0.0001` of `0.3` and invocation count is
  at most 20.
- [ ] Run the Task 9 search filter and confirm the new bracket/bisection tests fail.
- [ ] Implement the exact rules above. Do not sort `Evaluated`; it must preserve invocation order for
  diagnostics and tie-breaking.
- [ ] Re-run the Task 9 search filter and commit:

```bash
git add src/RouteTimer.Services/Adjustments/BoundedPacingSearch.cs tests/RouteTimer.Services.Tests/Adjustments/BoundedPacingSearchTests.cs
git commit -m "feat: add deterministic pacing bisection"
```

## Checkpoint 9.3: Normalized power

- [ ] Add tests named:
  `Calculate_rejects_invalid_segments`, `Calculate_uses_weighted_mean_below_thirty_seconds`,
  `Calculate_switches_at_exactly_thirty_seconds`, `Calculate_handles_thirty_point_zero_zero_one_seconds`,
  `Calculate_returns_constant_power_for_fractional_segments`,
  `Calculate_energy_weights_a_boundary_inside_a_bucket`, and
  `Calculate_weights_a_fractional_final_window`, and `Calculate_matches_the_variable_power_fixture`.

Use `PredictionSegment` fixtures with sequential sequence numbers and `ConfidenceLevel.High`. For a
29.999-second route containing 10 seconds at 100 W and 19.999 seconds at 200 W, calculate the expected
fallback directly:

```csharp
var expected = ((100d * 10d) + (200d * 19.999d)) / 29.999d;
Assert.Equal(expected, result.Watts, 9);
Assert.True(result.UsedShortRouteApproximation);
```

For the variable-power fixture, use 30 one-second segments at 100 W followed by 30 one-second segments
at 300 W. With windows ending at seconds 30 through 60 inclusive, assert NP
`223.06948877930961` within nine decimal places. This pins fourth-power/window ordering independently of
the production implementation.

- [ ] Run and confirm failure because `NormalizedPowerCalculator` does not exist:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~NormalizedPowerCalculatorTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

- [ ] Implement energy buckets and rolling-window integration. Keep this calculator pure; it must not
  know warning strings, FTP, IF, route geometry, or `IRoutePredictor`.
- [ ] Re-run both Task 9 test classes:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~BoundedPacingSearchTests|FullyQualifiedName~NormalizedPowerCalculatorTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] Commit and push:

```bash
git add src/RouteTimer.Services/Adjustments/NormalizedPowerCalculator.cs tests/RouteTimer.Services.Tests/Adjustments/NormalizedPowerCalculatorTests.cs
git commit -m "feat: add normalized power calculation"
git push
```

## Task 9 acceptance

- Grid order, bracket choice, tie-breaking, fallback, cancellation, and caps are deterministic.
- `Evaluated.Count` never exceeds `MaximumEvaluations`.
- Evaluator failures remain visible without aborting valid searches.
- NP has explicit behavior at 29.999, 30, and 30.001 seconds and for fractional segment boundaries.
- No production file outside `src/RouteTimer.Services/Adjustments/` changed.

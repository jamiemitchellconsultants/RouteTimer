[← Back to plan overview](README.md)

# Task 9: Implement bounded full-simulation search and normalized power primitives

**Files:**

- Create: `src/RouteTimer.Services/Adjustments/BoundedPacingSearch.cs`
- Create: `src/RouteTimer.Services/Adjustments/NormalizedPowerCalculator.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/BoundedPacingSearchTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/NormalizedPowerCalculatorTests.cs`

**Step 1: Write failing search tests**

Use a fake evaluator to prove fixed coarse-grid evaluation, adjacent sign-change bracket selection, bisection, exact-bound hits, closest-valid fallback without a bracket, invalid-candidate diagnostics, deterministic tie-breaking, cancellation between evaluations, and a hard evaluation cap.

**Step 2: Implement a strategy-neutral search result**

```csharp
public sealed record PacingSearchCandidate<T>(
    double Parameter,
    T? Value,
    double? Objective,
    string? FailureCode);

public sealed record PacingSearchResult<T>(
    PacingSearchCandidate<T> Selected,
    IReadOnlyList<PacingSearchCandidate<T>> Evaluated,
    bool Converged,
    bool Bracketed);
```

Require finite ordered bounds, fixed grid size, tolerance, and max evaluations. Never retry a parameter already evaluated.

**Step 3: Implement exact one-second NP resampling**

Expand piecewise-constant segment power onto one-second buckets, weighting partial first/last seconds. Compute a trailing 30-second rolling mean for each full window, raise each mean to the fourth power, average, then take the fourth root. Routes under 30 seconds return duration-weighted mean power plus the `np-if-short-route-approximation` warning rather than NaN.

**Step 4: Test duration boundaries**

Include 29.999 s, 30 s, 30.001 s, a constant-power route where NP equals power, unequal segment durations, fractional final seconds, and non-finite input rejection.

**Step 5: Run tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~BoundedPacingSearch|FullyQualifiedName~NormalizedPowerCalculator" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Services/Adjustments tests/RouteTimer.Services.Tests/Adjustments
git commit -m "feat: add bounded pacing search primitives"
```

**Step 6: Push and summarize**

```bash
git push
```

Summarize the change for this task: the strategy-neutral search primitive, its convergence/fallback rules, and the exact one-second NP resampling algorithm with its boundary test coverage. These primitives are shared by every remaining search-based strategy — flag anything a reviewer should confirm before it's reused.

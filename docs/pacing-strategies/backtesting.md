# Pacing Strategy Backtesting Evidence

## 1. Purpose and Synthetic vs Private Data
This document outlines deterministic backtesting verification for pacing strategy adjustments in RouteTimer. Deterministic CI tests use synthetic route and power model fixtures to ensure exact sequence parity, bounded simulation execution, non-negative finite power/speed, and non-empty algorithm version outputs without requiring private rider GPS/power payloads in the codebase.

## 2. Synthetic Fixture Matrix
The fixtures are defined once in `tests/RouteTimer.Services.Tests/Adjustments/PacingFixtures.cs` and driven by the harness in `PacingStrategyBacktestingTests.cs`:
- `flat-short`: 12 segments of 50m each at 0% gradient.
- `flat-long`: 120 segments of 100m each at alternating -0.5% and +0.5% gradient.
- `rolling`: 80 segments repeating -3%, 0%, +3%, +5% gradients.
- `mountainous`: 100 segments with sustained +6%/+9% climb blocks separated by descents.
- `fractional`: 31 segments producing fractional-second durations.

## 2a. Coverage
Every strategy handler runs on every fixture, so the matrix is 5 strategies x 5 fixtures. Each run must produce a well-formed computation: sequence parity with the baseline, finite non-negative power and speed, positive per-segment moving time, warning codes drawn from `AdjustmentWarningCodes`, annotations keyed only by real segment sequences, and a non-empty algorithm version. Strategy-specific assertions on top of that:
- Segment gains: replay parity only.
- Time target: the result is no further from the target than the baseline was, and evaluation count stays inside the bounded search budget.
- NP/IF: the reported NP matches a recomputation from the adjusted segments, and a non-converged search carries `np-if-closest-feasible`.
- Zone shift: every segment is annotated, the distribution sums to 100%, and per-assignment match counts follow submitted order.
- Match-burning: W' balance stays inside capacity, burn phases match the window's matched-segment count, and unmatched windows warn.

## 3. Execution & Verification Command
Run backtesting tests specifically via dotnet test:
```bash
dotnet test tests/RouteTimer.Services.Tests/ --filter "PacingStrategyBacktestingTests"
```

## 4. Manual Rollout Gates
When rolling out strategies with private historical dataset checks, verify:
- Time target achieved duration within 5% of target on test routes.
- NP/IF target time recovery within 3%.
- Zone distribution sums to 100%.
- Match-burning climb speeds within 5% of expected capacity models.

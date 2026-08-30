# Pacing Strategy Backtesting Evidence

## 1. Purpose and Synthetic vs Private Data
This document outlines deterministic backtesting verification for pacing strategy adjustments in RouteTimer. Deterministic CI tests use synthetic route and power model fixtures to ensure exact sequence parity, bounded simulation execution, non-negative finite power/speed, and non-empty algorithm version outputs without requiring private rider GPS/power payloads in the codebase.

## 2. Synthetic Fixture Matrix
The backtesting harness in `tests/RouteTimer.Services.Tests/Adjustments/PacingStrategyBacktestingTests.cs` utilizes the following synthetic fixtures:
- `flat-short`: 12 segments of 50m each at 0% gradient.
- `flat-long`: 120 segments of 100m each at alternating -0.5% and +0.5% gradient.
- `rolling`: 80 segments repeating -3%, 0%, +3%, +5% gradients.
- `mountainous`: 100 segments with sustained +6%/+9% climb blocks separated by descents.
- `fractional`: 31 segments producing fractional-second durations.

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

[← Back to plan overview](README.md)

# Task 13: Deliver variable match-burning end to end

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/MatchBurning/MatchBurningDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/MatchBurning/MatchBurningReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/CapacityResolver.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/MatchPhasePlanner.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/WPrimeBalanceCalculator.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/MatchBurningPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/MatchBurning/MatchBurningHandler.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/MatchBurningEditor.razor`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/CapacityResolverTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/MatchPhasePlannerTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/WPrimeBalanceCalculatorTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/MatchBurningHandlerTests.cs`
- Test: `tests/RouteTimer.Client.Tests/MatchBurningEditorTests.cs`
- Modify shared contract, mapping, DI, and report files.

**Step 1: Write failing capacity and phase tests**

Cover supplied CP/W-prime validation, model inference and its provenance, unavailable inference, phase distance boundaries, ordered first-match priority, ten-phase limit, hold/recovery/match targets, segment annotations, and deterministic optional one-pass refinement.

**Step 2: Implement exponential W-prime balance tests first**

Test depletion above CP:

```text
Wbal_next = max(0, Wbal - (P - CP) * dt)
```

and reconstitution below CP:

```text
DCP = CP - P
tau = 546 * exp(-0.01 * DCP) + 316
Wbal_next = Wprime - (Wprime - Wbal) * exp(-dt / tau)
```

Include exact CP, zero duration, large duration, floor/ceiling behavior, and non-finite rejection. Do not reintroduce the earlier fixed linear recovery approximation.

**Step 3: Implement handler flow**

Resolve capacity, precompute phase by sequence, run the full prediction once, calculate W-prime trajectory from actual segment durations, and optionally run one deterministic refinement when the configured reserve constraint is missed. Never loop refinement without a fixed cap of one.

**Step 4: Persist report and annotations**

Report supplied/inferred CP and W-prime with provenance, minimum/final W-prime balance, time above CP, work above CP, reserve breaches, phase summaries, whether refinement ran, and route-level deltas. Annotate each segment with phase and W-prime balance. Add known warnings for inferred capacity and reserve breach.

**Step 5: Add editor and disclaimer**

Allow supplied or inferred capacity and up to ten ordered phases. Explain that inferred values are model estimates for scenario comparison, not physiological measurements or training advice.

**Step 6: Run focused tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~MatchBurning|FullyQualifiedName~WPrime|FullyQualifiedName~CapacityResolver" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~MatchBurning -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src tests
git commit -m "feat: add variable match burning adjustments"
```

**Step 7: Push and summarize**

```bash
git push
```

Summarize the change for this task: the capacity resolver and provenance, the exponential W-prime balance model, the phase planner, and the one-pass refinement rule. This is a review checkpoint (highest-risk strategy) — call out the exponential recovery model and the refinement cap as the areas most worth a reviewer's scrutiny.

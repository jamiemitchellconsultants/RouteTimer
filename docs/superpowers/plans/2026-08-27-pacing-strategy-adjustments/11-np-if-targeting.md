[← Back to plan overview](README.md)

# Task 11: Deliver NP/IF targeting end to end

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/NpIf/NpIfTargetDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/NpIf/NpIfTargetReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/NpIf/NpIfPowerPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/NpIf/NpIfTargetHandler.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/NpIfTargetEditor.razor`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/NpIfTargetHandlerTests.cs`
- Test: `tests/RouteTimer.Client.Tests/NpIfTargetEditorTests.cs`
- Modify shared contract, mapping, DI, and report files.

**Step 1: Write failing strategy tests**

Cover explicit FTP validation, target IF bounds, proportional and additive modes, under-30-second mean-power fallback, under-ten-minute approximation warning, exact target, unreachable high/low target, no-bracket closest result, candidate failure recovery, evaluation cap, and cancellation.

**Step 2: Implement the objective**

Each candidate creates either a proportional or additive policy, calls the real `IRoutePredictor`, computes NP from the resulting segment durations, and evaluates:

```csharp
objective = normalizedPowerWatts - ftpWatts * targetIntensityFactor;
```

Use fixed bounds and tolerances from the approved design. The report stores requested FTP/IF, achieved NP/IF, mode, selected parameter, convergence, evaluation count, and route-level deltas. Add a known warning for closest-feasible fallback.

**Step 3: Add editor and report rendering**

Explain that FTP is an input to this adjustment and does not modify the rider model. Disable submit until the client's basic field validation passes; server validation remains authoritative.

**Step 4: Run focused tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~NpIf -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~NpIf -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src tests
git commit -m "feat: add normalized power targeting"
```

**Step 5: Push and summarize**

```bash
git push
```

Summarize the change for this task: the NP/IF objective function, proportional/additive modes, and the fallback/approximation warnings. This is a review checkpoint (the shared search family) — call out how this strategy exercises `BoundedPacingSearch` and `NormalizedPowerCalculator` from Task 9, and anything a reviewer should confirm before the remaining strategies reuse the same primitives.

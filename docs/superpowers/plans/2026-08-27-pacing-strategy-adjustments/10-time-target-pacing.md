[← Back to plan overview](README.md)

# Task 10: Deliver time-target pacing end to end

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/TimeTarget/TimeTargetDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/TimeTarget/TimeTargetReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/TimeTarget/TimeTargetPowerPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/TimeTarget/TimeTargetHandler.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/TimeTargetEditor.razor`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/TimeTargetHandlerTests.cs`
- Test: `tests/RouteTimer.Client.Tests/TimeTargetEditorTests.cs`
- Modify shared contract, mapping, DI, and report files.

**Step 1: Write failing distribution and feasibility tests**

Test proportional mode, climb-focused exact normalization, zero climb-weight fallback, no `EvenEffort` discriminator, faster and slower targets, targets equal to baseline, impossible targets, duration tolerance, hard evaluation cap, and closest-feasible reporting.

**Step 2: Implement exact climb-focused normalization**

Classify climbs as gradient at least 3%. Compute their fraction `f` of baseline moving time. For outer scale `S` and climb bias `b`, precompute `climbScale = S * b / (f * b + 1 - f)` and `otherScale = S / (f * b + 1 - f)`. This keeps the baseline-time-weighted mean factor exactly `S`. A route with no qualifying climb falls back to proportional and adds `time-target-no-climbs`.

**Step 3: Search complete simulations**

Use `adjusted.MovingTime.TotalSeconds - targetSeconds` as objective. Report the feasible interval obtained from bound candidates, requested/achieved time, absolute/percentage miss, distribution, scalar, convergence, evaluation count, gradient-band demand ratios, and the approved feasibility verdict. Reject nonsensical times before enqueue; publish closest valid with a warning when physically infeasible.

**Step 4: Add duration input and report UI**

Use an accessible `hh:mm:ss` editor with explicit parsing errors. Show faster/slower delta relative to baseline and label feasibility as a model result.

**Step 5: Run focused tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~TimeTarget -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~TimeTarget -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src tests
git commit -m "feat: add time target pacing"
```

**Step 6: Push and summarize**

```bash
git push
```

Summarize the change for this task: the climb-focused normalization math, the search objective and feasibility reporting, and the duration-input UI. Note anything about the feasibility verdict wording a reviewer should double-check.

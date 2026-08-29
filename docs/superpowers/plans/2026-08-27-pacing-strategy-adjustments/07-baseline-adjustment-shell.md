[← Back to plan overview](README.md)

# Task 7: Build the baseline-primary adjustment shell in the client

**Files:**

- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentBuilder.razor`
- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentList.razor`
- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentComparison.razor`
- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentSummaryCard.razor`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor.css`
- Modify: `src/RouteTimer.Client/Jobs/JobPoller.cs`
- Test: `tests/RouteTimer.Client.Tests/PredictionAdjustmentShellTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionDetailPageTests.cs`

**Step 1: Add failing bUnit tests for primary/secondary behavior**

Prove that:

- baseline summary and baseline visualization render first and never disappear;
- controls are absent for incomplete baselines or a disabled parent capability;
- multiple adjustments remain listed after another is created;
- only one adjustment can be selected for comparison;
- "Back to baseline" clears selection without deleting anything;
- queued/running children poll independently and terminal children stop polling;
- failed/cancelled children retain their row and readable state; and
- deleting a selected child returns to baseline and leaves siblings.

**Step 2: Implement state ownership in `PredictionDetail`**

The page owns `baseline`, `capabilities`, `adjustmentSummaries`, and `selectedAdjustmentId`. Child components receive immutable parameters and callbacks. Baseline load failure remains governed by existing behavior; adjustment-list failure shows an inline secondary error and must not hide the baseline.

**Step 3: Implement comparison semantics**

The comparison card labels columns "Baseline" and the strategy display name. Show deltas for moving time, average speed, and duration-weighted average power. Warnings and strategy reports belong only to the selected adjustment. Do not put adjusted GPX or Garmin actions in this card.

**Step 4: Run client tests and commit**

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionAdjustmentShell|FullyQualifiedName~PredictionDetailPage" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Client/Components/Adjustments src/RouteTimer.Client/Pages/PredictionDetail.razor src/RouteTimer.Client/Pages/PredictionDetail.razor.css src/RouteTimer.Client/Jobs tests/RouteTimer.Client.Tests
git commit -m "feat: add baseline adjustment comparison shell"
```

**Step 5: Push and summarize**

```bash
git push
```

Summarize the change for this task: the new client components, state ownership split, comparison semantics, and independent polling behavior. Note anything about baseline-primacy guarantees a reviewer should double-check.

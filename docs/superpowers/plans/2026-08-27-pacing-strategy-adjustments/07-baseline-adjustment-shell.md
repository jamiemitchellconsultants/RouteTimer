[← Back to plan overview](README.md)

# Task 7: Build the baseline-primary adjustment shell in the client

**Files:**

- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentBuilder.razor`
- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentList.razor`
- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentComparison.razor`
- Create: `src/RouteTimer.Client/Components/Adjustments/AdjustmentSummaryCard.razor`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- ~~Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor.css`~~ not needed (see note below)
- ~~Modify: `src/RouteTimer.Client/Jobs/JobPoller.cs`~~ not needed (see note below)
- Test: `tests/RouteTimer.Client.Tests/PredictionAdjustmentShellTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionDetailPageTests.cs`

**Implementation notes (deviations from plan):**

- **`JobPoller` needed no change.** `PollAsync(jobId, onUpdate, cancellationToken)` is already
  per-job-id and stateless between calls — `Predictions.razor` already runs it concurrently, once per
  in-flight baseline submission, via its own fire-and-forget `PollPredictionJobAsync` per prediction id.
  The same pattern already gives "queued/running children poll independently and terminal children stop
  polling" for free once a create flow exists to capture each new adjustment's job id — no shared state
  in `JobPoller` needed touching.
- **No live polling for pre-existing queued/running children, by design — matching existing precedent.**
  `Predictions.razor`'s own baseline list does not resume polling a queued prediction that was already
  in flight before the current page load either (`queuedPredictionIds` is only populated at the moment
  *this* session submits one); a pre-existing queued baseline just shows its static state until the next
  manual reload. The adjustment shell follows the same convention. Since `AdjustmentBuilder` cannot yet
  submit anything (see below), there is no create flow yet to capture a job id from in the first place —
  this becomes exercisable once a strategy is deliverable (Task 8+).
- **`AdjustmentBuilder` cannot create anything yet.** With zero concrete strategies delivered (see
  [Task 3](03-adjustment-domain-contracts.md)'s, [Task 5](05-adjustment-job-orchestration.md)'s, and
  [Task 6](06-nested-apis-and-capabilities.md)'s deviations), there is no strategy editor to plug in and
  every capability flag is `false` by default — so today it only ever renders its
  "not enabled"/"no strategies available" messages. It does already read `Capabilities` correctly and
  lists enabled strategy display names once any flag is `true`, ready for each strategy's own delivery
  task to register a real editor into it.
- **`PredictionDetail.razor.css` was not touched.** New markup in the adjustment components reuses the
  existing shared classes (`prediction-detail-panel`, `prediction-detail-grid`, `prediction-detail-list`,
  `prediction-detail-actions`, `predictions-button`/`--secondary`/`--danger`) rather than introducing new
  scoped styles, since Blazor CSS isolation only applies a page's own `.razor.css` to markup literally
  inside that page — new selectors there would not reach the new child components anyway.

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

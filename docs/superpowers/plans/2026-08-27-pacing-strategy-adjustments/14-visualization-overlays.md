[← Back to plan overview](README.md)

# Task 14: Add one-adjustment visualization overlays

**Files:**

- Modify: `src/RouteTimer.Client/Components/PredictionVisualization.razor`
- Modify: `src/RouteTimer.Client/Components/PredictionVisualization.razor.css`
- Modify: `src/RouteTimer.Client/wwwroot/js/prediction-visualization.js`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Modify: `tests/RouteTimer.Client.Tests/PredictionVisualizationTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionAdjustmentShellTests.cs`

**Step 1: Add failing rendering and interop tests**

Assert baseline-only rendering is byte-for-byte compatible at the component parameter boundary. With one selected adjustment, assert the interop payload contains aligned baseline/adjusted series by sequence, but never two adjusted series. Test missing/mismatched sequence rejection before JavaScript invocation.

**Step 2: Extend the chart payload**

Keep the baseline line visually dominant and stable. Draw the selected adjusted line with a distinct secondary style and legend label. Tooltips show baseline, adjusted, and delta for power/speed/time plus zone/phase/W-prime only when present.

**Step 3: Protect large-route behavior**

Reuse the existing downsampling path, preserving first/last points and strategy-boundary points. Do not perform an N-by-M sequence join in JavaScript; align once in C# and pass arrays.

**Step 4: Run client tests and commit**

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionVisualization|FullyQualifiedName~PredictionAdjustmentShell" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: visualize baseline adjustment comparisons"
```

**Step 5: Push and summarize**

```bash
git push
```

Summarize the change for this task: the extended chart payload, the C#-side sequence alignment (no JS N-by-M join), and confirmation that baseline-only rendering stayed unchanged. Note anything about large-route downsampling a reviewer should double-check.

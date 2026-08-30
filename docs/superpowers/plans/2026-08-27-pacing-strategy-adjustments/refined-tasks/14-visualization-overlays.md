[← Refined task index](README.md)

# Task 14: Add one-adjustment visualization overlays

**Correction to original Task 14:** There is no `prediction-visualization.js`. The current charts are
split across `PredictionVisualization.razor`, `RouteProfiles.razor`, `route-visualization.js`, and
`route-visualization-core.mjs`. Baseline-only JS interop must retain its current function and arguments.

## Files

- Modify: `src/RouteTimer.Client/Components/PredictionVisualization.razor`
- Modify: `src/RouteTimer.Client/Components/PredictionVisualization.razor.css`
- Modify: `src/RouteTimer.Client/Components/RouteProfiles.razor`
- Modify: `src/RouteTimer.Client/Pages/PredictionDetail.razor`
- Modify: `src/RouteTimer.Client/wwwroot/js/route-visualization.js`
- Modify: `src/RouteTimer.Client/wwwroot/js/route-visualization-core.mjs`
- Modify: `src/RouteTimer.Client/wwwroot/js/route-visualization.test.mjs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionVisualizationTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionAdjustmentShellTests.cs`

Do not modify `RouteMap.razor`, persistence/API contracts, baseline geometry, export actions, or map
polyline rendering. The map remains baseline-only.

## Component contract and alignment

Add this optional parameter to `PredictionVisualization` and `RouteProfiles`:

```csharp
[Parameter]
public IReadOnlyList<PredictionAdjustmentSegmentResponse> AdjustmentSegments { get; set; } = [];
```

`PredictionVisualization.OnParametersSet` continues sorting baseline segments. When adjustment segments
are present, sort them once and require exact sequence equality with the baseline. Do not use count-only
validation and do not join inside a render loop. Build a dictionary once for selected-segment lookup.

On mismatch:

- retain baseline map/charts and selected-segment readout;
- pass an empty adjustment list to `RouteProfiles` so no comparison JS function runs;
- render `data-testid="prediction-visualization-comparison-problem"` with “The selected adjustment does
  not match the baseline route.”;
- do not throw and do not hide the baseline.

When aligned, the selected readout renders baseline and adjusted power, speed, segment moving time, each
delta, and optional zone/phase/W-prime. Labels say “Baseline” and “Adjustment.” Null annotations are
omitted rather than displayed as zero/blank.

`PredictionDetail.razor` passes:

```razor
<PredictionVisualization
    Segments="orderedSegments"
    AdjustmentSegments="@(selectedAdjustmentDetail?.Segments ?? [])" />
```

Clearing/deleting selection therefore restores baseline-only rendering automatically.

## JS interop compatibility

Keep the existing baseline call byte-for-byte at the argument boundary:

```text
initializeProfiles(componentId, containerIds, Segments, dotNetReference)
```

When a valid adjustment exists, call a new export instead:

```text
initializeComparisonProfiles(componentId, containerIds, baselineSegments, adjustmentSegments, dotNetReference)
```

Include baseline and adjustment metrics in `RouteProfiles`' initialization signature so selecting a
different completed adjustment reinitializes charts even when sequence IDs are identical. Do not put
opaque JSON serialization in the signature; join stable invariant-culture scalar values.

In `route-visualization-core.mjs`, add:

```javascript
export function alignComparisonSegments(rawBaseline, rawAdjustment)
export function downsampleComparisonPoints(points, maximumPoints = 1500)
export function buildComparisonProfileDatasets(rawBaseline, rawAdjustment)
```

`alignComparisonSegments` normalizes baseline with the existing function, validates adjustment numeric
fields (`sequence`, `powerWatts`, `speedMetresPerSecond`, `segmentMovingSeconds`,
`cumulativeMovingSeconds`, `wPrimeBalanceJoules` when non-null), and returns one flat point per sequence.
It throws on missing, duplicate, or extra sequences.

Comparison downsampling applies only to the new comparison path; baseline-only datasets remain unchanged.
Always preserve first/last points and both sides of any change in `zoneNumber` or `strategyPhase`. Fill
remaining slots by evenly spaced indices, deduplicate indices, and return sequence order. If mandatory
boundary points exceed 1500, retain all mandatory points even though the result exceeds the target—the
semantic boundary is more important than the soft display cap.

`buildComparisonProfileDatasets` returns:

- elevation and gradient: one baseline dataset each;
- power and speed: baseline dataset first, adjustment dataset second;
- every point carries sequence, distance, baseline/adjusted segment time, deltas, and annotations so
  tooltips do no sequence search.

The returned array contains four objects in elevation/gradient/power/speed order with this stable shape:

```javascript
{
  label: "Power",
  suffix: " W",
  baselinePoints: [{ sequence, x, y, baselineSegmentMovingSeconds }],
  adjustmentPoints: [{
    sequence, x, y, baselineY, delta, baselineSegmentMovingSeconds,
    adjustmentSegmentMovingSeconds, segmentMovingSecondsDelta,
    zoneNumber, strategyPhase, wPrimeBalanceJoules
  }]
}
```

Elevation and gradient use an empty `adjustmentPoints` array. Power/speed `baselineY` and `delta` use the
same display unit as `y`; speed values/deltas are km/h.

In `route-visualization.js`, factor chart construction enough to accept one or two line datasets. Baseline
uses existing colour/width; adjustment uses `#d1495b`, width 2, no fill. Show a legend only on power/speed
comparison charts. Tooltip rows include baseline value, adjustment value, delta, segment time delta, and
present annotations. Hover selection uses the point's sequence and never assumes chart index equals raw
segment index.

## Checkpoint 14.1: C# alignment and selected readout

- [ ] Extend `PredictionVisualizationTests` with:
  `Baseline_only_keeps_existing_route_profile_arguments`,
  `Aligned_adjustment_is_passed_to_comparison_profiles`,
  `Mismatched_adjustment_shows_problem_and_never_invokes_comparison_profiles`, and
  `Selected_readout_shows_baseline_adjustment_deltas_and_annotations`.
- [ ] Strict JS interop must assert the existing baseline `initializeProfiles` invocation still has the
  current four arguments and the comparison invocation uses the new five-argument export.
- [ ] Add the parameter, alignment/dictionary, problem/readout, `RouteProfiles` branch, and page wiring.
- [ ] Shell tests select Adjustment A then B, assert only B's values appear, clear selection, and assert
  no adjustment values remain.
- [ ] Run:

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionVisualization|FullyQualifiedName~PredictionAdjustmentShell" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

- [ ] Commit: `feat: align adjustment visualization data`.

## Checkpoint 14.2: Comparison datasets and downsampling

- [ ] Node tests cover exact alignment, missing/duplicate/extra sequence rejection, non-finite adjusted
  metrics, four dataset groups, baseline-before-adjustment order, conversion to km/kmh, first/last
  preservation, phase/zone boundary preservation, 1500-point soft cap, and mandatory-boundary overflow.
- [ ] Start the comparison test with an adjustment in reverse order so it proves sequence alignment:

```javascript
test("buildComparisonProfileDatasets aligns adjustment by sequence", () => {
  const adjustment = [
    { sequence: 2, powerWatts: 275, speedMetresPerSecond: 9.2, segmentMovingSeconds: 58,
      cumulativeMovingSeconds: 116, zoneNumber: 4, strategyPhase: null, wPrimeBalanceJoules: null },
    { sequence: 1, powerWatts: 260, speedMetresPerSecond: 8.5, segmentMovingSeconds: 58,
      cumulativeMovingSeconds: 58, zoneNumber: 3, strategyPhase: null, wPrimeBalanceJoules: null }
  ];

  const datasets = buildComparisonProfileDatasets(rawSegments, adjustment);
  assert.deepEqual(datasets[2].adjustmentPoints.map(point => point.sequence), [1, 2]);
});
```

- [ ] Run and confirm failure before implementation:

```bash
npm test --prefix src/RouteTimer.Client
```

- [ ] Implement core functions without DOM, Chart.js, or Leaflet dependencies. Preserve existing exports
  and existing baseline test expected values.
- [ ] Re-run Node tests and commit: `feat: build adjustment comparison datasets`.

## Checkpoint 14.3: Chart rendering and full client verification

- [ ] Implement `initializeComparisonProfiles` and dual-line chart/tooltip behavior. Keep
  `initializeProfiles`, `selectProfileSequence`, and disposal behavior compatible.
- [ ] Add/adjust CSS only for selected-readout layout and a small visual key; do not duplicate Chart.js
  legend colours in Razor.
- [ ] Run:

```bash
npm test --prefix src/RouteTimer.Client
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] Commit and push:

```bash
git add src/RouteTimer.Client tests/RouteTimer.Client.Tests
git commit -m "feat: visualize baseline adjustment comparisons"
git push
```

## Task 14 acceptance

- Baseline-only interop and chart datasets remain on the existing code path.
- Exactly zero or one adjustment is rendered; there is no collection-of-adjustments chart API.
- Alignment happens once in C#, core JS validation remains defensive, and no N-by-M lookup occurs.
- Elevation, gradient, geometry, exports, and map stay baseline-primary.
- Large comparison charts preserve semantic phase/zone boundaries.

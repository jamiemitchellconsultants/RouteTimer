[← Back to plan overview](README.md)

# Task 1: Introduce `PredictionRoute` without changing baseline output

**Files:**

- Create: `src/RouteTimer.Domain/Predictions/PredictionRoute.cs`
- Modify: `src/RouteTimer.Services/Predictions/IRoutePredictor.cs`
- Modify: `src/RouteTimer.Services/Predictions/RoutePredictor.cs`
- Modify: `src/RouteTimer.Services/Predictions/PredictionJobHandler.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionFixtures.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/RoutePredictorTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionWorkflowTests.cs`

**Step 1: Add a failing baseline-parity test**

Before changing the predictor signature, capture the current mixed-route result as an explicit golden `PredictionResult` fixture (including every segment and warning). Map the same input samples with `Skip(1)` into the proposed route and compare every public field and warning in order:

```csharp
[Fact]
public void PredictionRoute_refactor_preserves_the_complete_baseline_result()
{
    var processed = PredictionFixtures.MixedProcessedRoute();
    var expected = PredictionFixtures.MixedRouteGoldenResult();

    var actual = PredictionFixtures.Predict(PredictionRoute.FromProcessed(processed));

    Assert.Equal(expected, actual);
}
```

This must fail to compile before the route type exists.

**Step 2: Run the focused tests and record the expected failure**

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~RoutePredictor -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: compile failure naming `PredictionRoute`.

**Step 3: Add the simulation-only route records**

Implement immutable records with constructor validation for a non-empty, contiguous sequence and finite non-negative route values:

```csharp
public sealed record PredictionRoute(
    IReadOnlyList<PredictionRouteSegment> Segments,
    double DistanceMetres,
    double AscentMetres);

public sealed record PredictionRouteSegment(
    int Sequence,
    double Latitude,
    double Longitude,
    double ElevationMetres,
    double CumulativeDistanceMetres,
    double SegmentDistanceMetres,
    double Gradient,
    double CurvaturePerMetre);
```

Put mapping functions at orchestration boundaries: `PredictionJobHandler` maps `ProcessedRoute.Samples.Skip(1)`, while later the adjustment job maps persisted baseline segments. Do not make the domain type depend on parser-specific `ProcessedRoute`.

**Step 4: Change the predictor signature and remove `Skip(1)` from its loop**

```csharp
PredictionResult Predict(
    PredictionRoute route,
    RiderProfile profile,
    RiderModel model,
    CancellationToken cancellationToken = default);
```

The loop consumes `route.Segments` directly. Preserve validation order, warning order, entry-speed state, duration-band lookup, confidence aggregation, and all existing error translation.

**Step 5: Run all service tests**

Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: all service tests pass, including the parity test.

**Step 6: Commit**

```bash
git add src/RouteTimer.Domain/Predictions/PredictionRoute.cs src/RouteTimer.Services/Predictions tests/RouteTimer.Services.Tests/Predictions
git commit -m "refactor: make prediction routes replayable"
```

**Step 7: Push and summarize**

```bash
git push
```

Summarize the change for this task: what was added or modified, why (baseline parity preserved while making the route replayable), and the test evidence that supports it. Keep the summary available for the next review checkpoint.

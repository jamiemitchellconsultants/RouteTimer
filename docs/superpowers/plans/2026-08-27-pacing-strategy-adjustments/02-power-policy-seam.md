[← Back to plan overview](README.md)

# Task 2: Add the segment-aware power-policy seam

**Files:**

- Create: `src/RouteTimer.Domain/Predictions/PowerTargetContext.cs`
- Create: `src/RouteTimer.Services/Predictions/IPowerTargetPolicy.cs`
- Modify: `src/RouteTimer.Services/Predictions/IRoutePredictor.cs`
- Modify: `src/RouteTimer.Services/Predictions/RoutePredictor.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/RoutePredictorTests.cs`

**Step 1: Add failing policy tests**

Add tests proving:

1. a `null` policy is bit-for-bit identical to the baseline;
2. the policy sees the full segment, elapsed moving time before that segment, and the untouched model estimate;
3. the resolved power changes duration through the real physics loop; and
4. negative, NaN, or infinite policy output becomes `PredictionCalculationException`.

Use a recording policy rather than a mocked predictor:

```csharp
private sealed class RecordingPolicy(Func<PowerTargetContext, PowerEstimate> resolve)
    : IPowerTargetPolicy
{
    public List<PowerTargetContext> Contexts { get; } = [];

    public PowerEstimate Resolve(PowerTargetContext context)
    {
        Contexts.Add(context);
        return resolve(context);
    }
}
```

**Step 2: Verify the tests fail for the missing seam**

Run the same focused `RoutePredictor` test command from Task 1. Expected: compile failure for `IPowerTargetPolicy`.

**Step 3: Implement the policy seam**

Add `IPowerTargetPolicy? powerTargetPolicy = null` before the cancellation token. For every segment:

```csharp
var baseline = powerLookup.Estimate(segment.Gradient, elapsedMovingTime);
var estimate = powerTargetPolicy?.Resolve(
    new PowerTargetContext(segment, elapsedMovingTime, baseline)) ?? baseline;
ValidatePowerEstimate(estimate);
```

Keep `PowerLookup` construction inside the predictor so all policies modify the captured model estimate instead of substituting a model.

**Step 4: Run service tests and commit**

Run all service tests, then:

```bash
git add src/RouteTimer.Domain/Predictions src/RouteTimer.Services/Predictions tests/RouteTimer.Services.Tests/Predictions
git commit -m "feat: add prediction power target policies"
```

**Step 5: Push and summarize**

```bash
git push
```

Summarize the change for this task: the new policy seam, how baseline behavior is guaranteed unchanged when no policy is supplied, and the test evidence for the four proven behaviors above. This checkpoint validates baseline parity — call out anything a reviewer should double-check.

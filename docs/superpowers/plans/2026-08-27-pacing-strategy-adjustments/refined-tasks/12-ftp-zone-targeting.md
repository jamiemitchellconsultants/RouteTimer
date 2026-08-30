[← Refined task index](README.md)

# Task 12: Deliver FTP-based and model-inferred zone targeting end to end

**Deliverable:** The `rpe-zone-shift` request name remains stable, but implementation and UI describe
power-zone targeting—not calculated subjective RPE.

## Files

- Create: `src/RouteTimer.Domain/Adjustments/Zones/ZoneShiftDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/Zones/ZoneShiftReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/Zones/PowerZoneResolver.cs`
- Create: `src/RouteTimer.Services/Adjustments/Zones/ZoneShiftPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/Zones/ZoneShiftHandler.cs`
- Create: `src/RouteTimer.Api/Adjustments/ZoneShiftRequestMapper.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/ZoneShiftEditor.razor`
- Create: `tests/RouteTimer.Services.Tests/Adjustments/Zones/PowerZoneResolverTests.cs`
- Create: `tests/RouteTimer.Services.Tests/Adjustments/Zones/ZoneShiftHandlerTests.cs`
- Create: `tests/RouteTimer.Client.Tests/ZoneShiftEditorTests.cs`
- Modify: `src/RouteTimer.Api/Endpoints/PredictionAdjustmentEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Client/Components/Adjustments/AdjustmentBuilder.razor`
- Modify: `src/RouteTimer.Client/Components/Adjustments/AdjustmentComparison.razor`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/PredictionAdjustmentShellTests.cs`

## Exact domain types

```csharp
namespace RouteTimer.Domain.Adjustments.Zones;

public enum ZoneThresholdMode { FtpBased, ModelInferred }
public enum ZonePlacement { LowerBound, Midpoint, UpperBound }
public enum ZoneThresholdProvenance { SuppliedFtp, InferredModel }

public sealed record ZoneAssignment
{
    public ZoneAssignment(bool allSegments, double? minGradient, double? maxGradient, int zone, ZonePlacement placement);
    public bool AllSegments { get; }
    public double? MinGradient { get; }
    public double? MaxGradient { get; }
    public int Zone { get; }
    public ZonePlacement Placement { get; }
    public bool Matches(double gradient);
}

public sealed record ZoneShiftDefinition : PacingStrategyDefinition
{
    public const int MaximumAssignments = 10;
    public ZoneShiftDefinition(ZoneThresholdMode thresholdMode, double? ftpWatts, IReadOnlyList<ZoneAssignment> assignments);
    public ZoneThresholdMode ThresholdMode { get; }
    public double? FtpWatts { get; }
    public IReadOnlyList<ZoneAssignment> Assignments { get; }
}

public sealed record ResolvedPowerZone(int Zone, double LowerWatts, double UpperWatts, double LowerTargetWatts, double MidpointTargetWatts, double UpperTargetWatts);
public sealed record ResolvedPowerZoneSet(
    double ThresholdWatts,
    ZoneThresholdProvenance Provenance,
    IReadOnlyList<ResolvedPowerZone> Zones);
public sealed record ZoneDistributionEntry(int Zone, double MovingSeconds, double Percentage);

public sealed record ZoneShiftReport(
    double ResolvedThresholdWatts,
    ZoneThresholdProvenance Provenance,
    IReadOnlyList<ResolvedPowerZone> Boundaries,
    IReadOnlyList<int> AssignmentMatchCounts,
    IReadOnlyList<ZoneDistributionEntry> Distribution,
    double AveragePowerWatts,
    double NormalizedPowerWatts,
    double MovingTimeDeltaSeconds,
    double AverageSpeedDeltaMetresPerSecond,
    double AveragePowerDeltaWatts)
    : PacingStrategyReport(PacingStrategyType.RpeZoneShift);
```

Definition validation:

- `FtpBased` requires finite FTP `[1,2000]`; `ModelInferred` requires null FTP.
- One to ten non-null assignments are required.
- At most one assignment has `AllSegments = true`.
- An all-segments assignment has null gradient bounds. A gradient assignment has at least one finite
  bound, ordered when both exist.
- FTP mode accepts zones 1–7; inferred mode accepts zones 1–5. Validate zone range at definition
  construction because mode is needed there.
- placement and mode must be defined.

## Authoritative resolver

Create this exact resolver in namespace `RouteTimer.Services.Adjustments.Zones`; do not return parallel
arrays:

```csharp
public static class PowerZoneResolver
{
    public static ResolvedPowerZoneSet Resolve(
        ZoneThresholdMode mode,
        double? ftpWatts,
        PowerModel model);

    public static double SelectTarget(ResolvedPowerZone zone, ZonePlacement placement);
}
```

Threshold is supplied FTP or `model.GlobalTypicalWatts / 0.83`. Reject non-positive/non-finite inferred
threshold as `ArgumentException`.

Use threshold fractions:

| Zone | FTP range | Inferred range |
| --- | --- | --- |
| 1 | 0%–55% | 0%–55% |
| 2 | 55%–75% | 55%–75% |
| 3 | 75%–90% | 75%–90% |
| 4 | 90%–105% | 90%–105% |
| 5 | 105%–120% | 105%–150% |
| 6 | 120%–150% | absent |
| 7 | above 150% | absent |

For closed zones, lower target is lower boundary + 5 W (Zone 1 uses 5 W), upper target is upper
boundary - 5 W, and midpoint is the arithmetic midpoint of boundaries. If a zone is narrower than
10 W, collapse all three targets to its midpoint. Zone 7 uses 151%, 160%, and 200% of threshold for
lower/mid/upper targets. Report `LowerWatts` as 150% threshold and `UpperWatts` as 200% threshold so
stored historical JSON is finite; selecting Zone 7 UpperBound adds `rpe-zone-z7-capped`.

For classifying actual adjusted watts, lower bounds are inclusive and upper bounds are exclusive; Zone 1
includes zero and the final available zone includes its reported upper boundary. Values above the final
reported boundary clamp to the highest available zone. Negative/non-finite adjusted watts are rejected
before distribution construction.

## Matching and report rules

At definition construction, preserve request order inside two groups: gradient assignments first, then
the optional all-segments fallback. This makes precedence independent of where the fallback appeared in
the request while preserving first-match order among gradient overrides. `ZoneShiftPolicy` uses the first
match, otherwise returns the unchanged baseline estimate. It annotates every matched adjusted segment
with the assigned zone; after simulation, unmatched segments are classified into the resolved zone whose
range contains their actual adjusted watts, clamping above the finite Zone 7 display ceiling to Zone 7.

Model-inferred mode always adds `rpe-zone-threshold-inferred`. Add `rpe-zone-model-low-confidence` when
`PowerModel.Bands` is empty or any band used to infer the global typical value has Low confidence; because
`GlobalTypicalWatts` has no direct provenance link, define this deterministically as “no bands or all bands
have `ConfidenceLevel.Low`.”

Calculate NP with Task 9. Distribution is duration-weighted, includes every resolved zone (zero entries
allowed), and percentages sum to 100 within `1e-9` before display rounding.

Use these service interfaces:

```csharp
public sealed class ZoneShiftPolicy(
    ZoneShiftDefinition definition,
    ResolvedPowerZoneSet zones) : IPowerTargetPolicy
{
    public IReadOnlyList<int> MatchCounts { get; }
    public IReadOnlyDictionary<int, int> AssignedZonesBySequence { get; }
    public bool UsedCappedZoneSevenTarget { get; }
    public PowerEstimate Resolve(PowerTargetContext context);
}

public sealed class ZoneShiftHandler(IRoutePredictor routePredictor) : IPacingStrategyHandler
{
    public const string AlgorithmVersion = "zone-shift-v1";
    public PacingStrategyType Type => PacingStrategyType.RpeZoneShift;
    public string Canonicalize(PacingStrategyDefinition strategy) =>
        PacingStrategyJson.Canonicalize((ZoneShiftDefinition)strategy);
    public PacingStrategyDefinition Deserialize(string canonicalJson) =>
        PacingStrategyJson.Deserialize<ZoneShiftDefinition>(canonicalJson);
    public string CanonicalizeReport(PacingStrategyReport report) =>
        PacingStrategyJson.CanonicalizeReport((ZoneShiftReport)report);
}
```

`ZoneShiftHandler.Run(PacingStrategyContext, PacingStrategyDefinition, CancellationToken)` performs one
full predictor call and returns `PacingStrategyComputation` using the report/matching rules above.

## Checkpoint 12.1: Domain and resolver

- [ ] Write definition tests for mode/FTP combinations, zero/ten/eleven assignments, duplicate fallback,
  selector validation, zone ranges, placement enum, and gradient-before-fallback ordering.
- [ ] Write resolver tests at FTP 300 W covering all seven zones and exact target formulas; inferred
  threshold from 249 W must be 300 W; test five inferred zones and narrow-zone collapse.

```csharp
[Fact]
public void Resolve_builds_the_finite_zone_seven_targets_from_ftp()
{
    var set = PowerZoneResolver.Resolve(
        ZoneThresholdMode.FtpBased,
        300,
        new PowerModel([], 249));
    var zoneSeven = Assert.Single(set.Zones, zone => zone.Zone == 7);

    Assert.Equal(450, zoneSeven.LowerWatts, 9);
    Assert.Equal(453, zoneSeven.LowerTargetWatts, 9);
    Assert.Equal(480, zoneSeven.MidpointTargetWatts, 9);
    Assert.Equal(600, zoneSeven.UpperTargetWatts, 9);
}
```

- [ ] Run and confirm missing types, then implement only domain records/resolver:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~PowerZoneResolver|FullyQualifiedName~ZoneShiftDefinition" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

- [ ] Commit: `feat: add power zone resolver`.

## Checkpoint 12.2: Policy, handler, mapping, and registration

- [ ] Handler tests cover first matching gradient override, fallback, unmatched baseline preservation,
  lower/mid/upper placement, Zone 7 cap/warning, inferred warnings, annotations, distribution totals, NP,
  route deltas, stable algorithm version `zone-shift-v1`, and JSON round-trip.
- [ ] Implement `ZoneShiftPolicy` and `ZoneShiftHandler`; call the real predictor once—this strategy does
  not use bounded search.
- [ ] Create `ZoneShiftRequestMapper`, parsing exact ordinal wire literals `ftp-based`/`model-inferred`
  and `lower-bound`/`midpoint`/`upper-bound`. Map each `ZoneAssignmentRequest` and return errors
  keyed `assignments[N]`; definition-level errors use `assignments`, `thresholdMode`, or `ftpWatts`.
- [ ] Replace the zone `NotImplementedException` arm, catch mapper errors, add valid/invalid API tests,
  and register handler in `Program.cs` with flag still false.
- [ ] Run focused Services/API tests. Commit: `feat: add power zone adjustments`.

## Checkpoint 12.3: Ordered editor and distribution report

- [ ] Editor IDs use prefix `zone-shift-`: `threshold-mode`, `ftp`, `add-assignment`, row `N` fields
  `selector`, `min`, `max`, `zone`, `placement`, `remove`, plus `submit` and indexed `error-N`.
- [ ] Default to model-inferred, blank FTP, and one all-segments Zone 3 midpoint assignment. Submit the
  exact wire strings above. Switching to
  FTP shows FTP and allows zones 1–7; inferred allows 1–5. Switching selector clears gradient bounds.
  Prevent a second all-segments assignment and an eleventh row.
- [ ] Submit the contract's exact `RpeZoneShiftRequest`/`ZoneAssignmentRequest` shape and render server
  errors beside rows.
- [ ] Report rendering states “threshold inferred from the captured power model” or “supplied FTP,”
  shows boundaries, time/percentage per zone, NP, averages, and route deltas. It must not claim to measure
  RPE or prescribe training.
- [ ] Add capability gating and bUnit tests for all state transitions/request/report rendering.
- [ ] Run:

```bash
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~ZoneShift|FullyQualifiedName~PredictionAdjustmentShell" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~ZoneShift|FullyQualifiedName~PowerZoneResolver" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] Commit and push:

```bash
git add src tests
git commit -m "feat: add power zone pacing UI"
git push
```

## Task 12 acceptance

- One resolver owns all percentage constants; the client reads report boundaries and never duplicates
  them.
- Historical reports contain finite thresholds/boundaries and explicit provenance.
- Gradient overrides precede fallback deterministically; unmatched segments retain model power.
- Segment annotations and distribution are derived from the actual adjusted result.

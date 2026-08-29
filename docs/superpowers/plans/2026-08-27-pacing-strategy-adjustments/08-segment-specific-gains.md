[← Back to plan overview](README.md)

# Task 8: Deliver segment-specific gains end to end

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/SegmentGains/SegmentGainsDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/SegmentGains/SegmentGainsReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/SegmentGains/SegmentGainsPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/SegmentGains/SegmentGainsHandler.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/SegmentGainsEditor.razor`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/SegmentGainsHandlerTests.cs`
- Test: `tests/RouteTimer.Client.Tests/SegmentGainsEditorTests.cs`
- Modify contract, API mapping, DI, dispatcher, and report rendering files created in Tasks 3, 5, 6, and 7.

**Step 1: Write failing rule tests**

Cover each selector (`Distance`, `Gradient`, `Sequence`), inclusive boundaries, ordered first-match precedence, one-selector-only validation, exactly one of factor/delta, negative deltas, 10 W floor, unchanged unmatched segments, ten-rule limit, and rule hit counts.

**Step 2: Implement deterministic matching**

Canonicalize rules in submitted order. Precompute `sequence -> applied rule ID` from route geometry, then resolve:

```csharp
var watts = rule.Factor is { } factor
    ? context.BaselineEstimate.Watts * factor
    : context.BaselineEstimate.Watts + rule.DeltaWatts!.Value;
return context.BaselineEstimate with { Watts = Math.Max(10, watts) };
```

Preserve the baseline estimate's evidence and confidence. The report contains matched/unmatched segment counts, per-rule hit count, and route-level deltas.

**Step 3: Add the editor and report**

Allow adding, ordering, and removing up to ten rules. Switching selector or adjustment mode clears fields belonging to the old choice before submit. Render server field errors next to the owning rule.

**Step 4: Run focused service/API/client tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~SegmentGains -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter FullyQualifiedName~SegmentSpecificGains -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~SegmentGains -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src tests
git commit -m "feat: add segment-specific pacing gains"
```

**Step 5: Push and summarize**

```bash
git push
```

Summarize the change for this task: the first complete vertical slice (domain, service, API, client) for a strategy, the deterministic rule-matching behavior, and the test evidence across all layers. This is a review checkpoint (first complete vertical slice) — call out anything that should set the pattern for the remaining strategies.

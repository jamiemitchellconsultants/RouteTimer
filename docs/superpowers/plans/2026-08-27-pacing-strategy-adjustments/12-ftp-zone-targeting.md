[← Back to plan overview](README.md)

# Task 12: Deliver FTP and inferred zone targeting end to end

**Files:**

- Create: `src/RouteTimer.Domain/Adjustments/Zones/ZoneShiftDefinition.cs`
- Create: `src/RouteTimer.Domain/Adjustments/Zones/ZoneShiftReport.cs`
- Create: `src/RouteTimer.Services/Adjustments/Zones/PowerZoneResolver.cs`
- Create: `src/RouteTimer.Services/Adjustments/Zones/ZoneShiftPolicy.cs`
- Create: `src/RouteTimer.Services/Adjustments/Zones/ZoneShiftHandler.cs`
- Create: `src/RouteTimer.Client/Components/Adjustments/ZoneShiftEditor.razor`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/PowerZoneResolverTests.cs`
- Test: `tests/RouteTimer.Services.Tests/Adjustments/ZoneShiftHandlerTests.cs`
- Test: `tests/RouteTimer.Client.Tests/ZoneShiftEditorTests.cs`
- Modify shared contract, mapping, DI, and report files.

**Step 1: Write failing zone-boundary tests**

Cover all seven FTP zone boundaries, finite upper target for zone 7, all five inferred zones, threshold inference `GlobalTypicalWatts / 0.83`, supplied-versus-inferred mode validation, ordered gradient assignments before the all-segments fallback, unmatched segments remaining unchanged, selected lower/midpoint/upper targets within the resolved zone, and duration-weighted zone distribution totaling 100% within rounding tolerance.

**Step 2: Implement one authoritative resolver**

Return both absolute watt boundaries and provenance (`SuppliedFtp` or `InferredModel`). Avoid duplicating percentages in the client. Persist the resolved threshold and boundaries in the report so historical adjustments remain explainable if constants change.

**Step 3: Implement policy and report**

Evaluate ordered gradient assignments before the optional all-segments assignment. For the first match, choose the requested finite lower-bound, midpoint, or upper-bound target in the requested zone; unmatched segments retain the baseline estimate. Preserve confidence/evidence. Annotate each adjusted segment with the resulting zone number and add `rpe-zone-z7-capped` when the finite Zone 7 ceiling is selected.

**Step 4: Add editor and distribution report**

Let the rider choose FTP-based or model-inferred zones and manage up to ten ordered assignments with gradient/all-segments selectors, zone, and placement. Show the provenance disclaimer and render duration and percentage by zone.

**Step 5: Run focused tests and commit**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~ZoneShift|FullyQualifiedName~PowerZoneResolver" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~ZoneShift -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src tests
git commit -m "feat: add power zone pacing adjustments"
```

**Step 6: Push and summarize**

```bash
git push
```

Summarize the change for this task: the authoritative zone resolver and its provenance tracking, the ordered gradient-assignment policy, and the distribution report. Note anything about zone-boundary edge cases or the Zone 7 cap a reviewer should double-check.

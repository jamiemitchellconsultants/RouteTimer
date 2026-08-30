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

**Implementation notes (deviations from plan):**

- **The domain rule mirrors the wire request almost 1:1, rather than exposing an already-resolved
  `Selector` enum as its public shape.** `SegmentGainsRule`'s constructor takes the same three bound
  pairs (gradient/sequence/distance) as `SegmentGainsRuleRequest` and validates "exactly one selector"
  itself, deriving `Selector` internally. This let the one-selector-only and exactly-one-of-factor/delta
  validations live in `tests/RouteTimer.Services.Tests/Adjustments/SegmentGains/SegmentGainsHandlerTests.cs`
  as pure domain-constructor tests, and made the API-layer mapper close to a pass-through.
- **The API-to-domain mapping needed its own new file, not just a `MapDefinition` edit.**
  `RouteTimer.Services` cannot reference `RouteTimer.Contracts` (contracts stay Domain-free per the
  established layering), so `SegmentSpecificGainsRequest -> SegmentGainsDefinition` mapping can only
  live in `RouteTimer.Api`, the one project allowed to reference both. Added
  `src/RouteTimer.Api/Adjustments/SegmentGainsRequestMapper.cs` with a dedicated
  `SegmentGainsRequestValidationException` carrying `rules[N]`-keyed field errors, wired into
  `PredictionAdjustmentEndpoints.CreateAdjustmentAsync` as `Results.ValidationProblem` - this is what
  "Render server field errors next to the owning rule" actually resolves to end to end.
- **`PacingStrategyJson` gained a `CanonicalizeReport<T>` generic, not just `Canonicalize<T>`/`Deserialize<T>`.**
  Task 3 only anticipated definitions needing canonical JSON; reports need it too so
  `IPacingStrategyHandler.CanonicalizeReport` has a canonical implementation to call. No byte-size limit
  is enforced for reports (they're handler-produced, not user-submitted).
- **The adjusted route is recomputed through `IRoutePredictor` with a custom `IPowerTargetPolicy`, not a
  naive per-segment power/speed formula.** `SegmentGainsPolicy` implements `IPowerTargetPolicy` (the
  seam added in [Task 2](02-power-target-policy.md)) and is handed to `RoutePredictor.Predict` alongside
  the baseline's own route/profile/model, so the adjusted result's speed and moving time come from the
  same physics as the baseline, not an approximation. This is expected to be every future strategy's own
  approach too - the seam was built exactly for this.
- **The handler's own `Warnings` list is built from scratch, not forwarded from the recomputed
  `PredictionResult.Warnings`.** `RoutePredictor.Predict`'s own warnings use `PredictionWarningCodes`
  (e.g. `power-model-extrapolation`), a vocabulary `PredictionAdjustmentJobHandler.BuildPublication`
  explicitly rejects for `PacingStrategyComputation.Warnings` (only `AdjustmentWarningCodes` are
  accepted, by design - see [Task 3](03-adjustment-domain-contracts.md)). `SegmentGainsHandler.Run`
  discards the recomputed result's own warnings and adds only `segment-gains-no-rules`/
  `segment-gains-power-clamped` when applicable.
- **`AdjustmentComparison.razor` renders the segment-gains report via a `StrategyType`-keyed switch on an
  opaque `JsonElement`, not a shared report-rendering abstraction.** Every report reaches the client as
  `JsonElement` (Task 6's deviation), so segment gains is the first strategy to actually render its own
  shape; the next strategy gets its own conditional block rather than a premature shared interface for
  reports that didn't exist until now.

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

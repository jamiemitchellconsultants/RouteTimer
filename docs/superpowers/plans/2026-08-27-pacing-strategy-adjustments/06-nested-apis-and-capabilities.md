[← Back to plan overview](README.md)

# Task 6: Expose nested APIs and capabilities

**Files:**

- Create: `src/RouteTimer.Contracts/Adjustments/PredictionAdjustmentContracts.cs` (moved here from [Task 3](03-adjustment-domain-contracts.md); see that task's implementation note)
- Create: `src/RouteTimer.Api/Adjustments/PacingStrategyOptions.cs`
- Create: `src/RouteTimer.Api/Endpoints/PredictionAdjustmentEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Api/appsettings.json`
- ~~Modify: `src/RouteTimer.Api/appsettings.Development.json`~~ not needed (see note below)
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`
- Modify: `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`
- Test: `tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/Api/RouteTimerApiClientTests.cs`

**Implementation notes (deviations from plan):**

- **`appsettings.Development.json` needed no change.** It only carries settings that *differ* from
  the shipped default for local dev (Garmin/Keycloak endpoints, auth mode) — `PacingStrategies`'
  defaults (everything disabled) are already the correct dev-time value, so there is nothing to
  override.
- **No genuine POST-succeeds-with-202 happy path is testable yet, and that's by design.** With zero
  concrete strategies delivered (per [Task 3](03-adjustment-domain-contracts.md)'s and
  [Task 5](05-adjustment-job-orchestration.md)'s deviations), every strategy type is permanently
  unreachable past the disabled-check today — `PacingStrategyOptions` defaults every flag to `false`,
  and nothing in `Program.cs` can ever set one `true` until a strategy exists to enable. The endpoint's
  `MapDefinition` switch has one arm per contract type, each throwing `NotImplementedException` for now;
  each is genuinely unreachable (the disabled-check always returns 409 first) until that strategy's own
  delivery task (8, 10, 11, 12, 13) replaces its arm with real mapping to a concrete domain type. Task 8
  is explicitly the plan's own "first complete vertical slice" checkpoint, so proving the full
  201/Location happy path there — rather than faking it here with a test-only handler — is consistent
  with the plan's own structure, not a shortfall of this task. Everything else is fully tested now:
  capability reporting, parent-vs-per-strategy disabled gating, malformed/unrecognized-discriminator
  JSON (a `NotSupportedException` for a missing/unknown polymorphic discriminator, not `JsonException`
  — both are now caught), nested-ownership 404s on detail/delete, and newest-first listing.
- **The request-serialization footgun is real and was caught by the tests themselves, not designed
  around in advance.** `HttpClient.PostAsJsonAsync<TValue>` and `JsonContent.Create<T>` infer `TValue`
  from the *compile-time* type of the argument expression, not the object's runtime type — passing a
  concrete `TimeTargetRequest` to either without an explicit `<PacingStrategyRequest>` type argument
  serializes it without the `"type"` discriminator at all, since polymorphism attributes live only on
  the abstract base. `RouteTimerApiClient.CreatePredictionAdjustmentAsync` therefore builds its own
  `JsonContent` explicitly typed as `PacingStrategyRequest` instead of reusing the existing generic
  `SendJsonAsync<T>(method, path, object payload, ct)` helper (whose `object`-typed parameter would hit
  the same bug). The discriminator's actual wire value is also the literal declared in
  `[JsonDerivedType(..., "time-target")]` (kebab-case, e.g. `"time-target"`), not the camelCase-transformed
  `"timeTarget"` one might expect from `JsonNamingPolicy.CamelCase` — that policy governs property names,
  not derived-type discriminator string literals. A client test asserting the literal wire body caught
  both of these before they could reach a real strategy's editor UI in Task 8+.

**Step 1: Add failing endpoint contract tests**

Test all five routes:

```text
GET    /api/pacing-strategies
POST   /api/predictions/{predictionId}/adjustments
GET    /api/predictions/{predictionId}/adjustments
GET    /api/predictions/{predictionId}/adjustments/{adjustmentId}
DELETE /api/predictions/{predictionId}/adjustments/{adjustmentId}
```

Assert nested ownership returns 404 rather than leaking that an adjustment belongs to another baseline. Assert POST returns 202 with `Location` pointing to the nested detail, and list ordering is newest-first. Assert parent/per-strategy disabled, malformed discriminator, >64 KiB payload, baseline not ready, and list-limit failures map to stable Problem Details codes.

**Step 2: Implement feature options and capability response**

Bind:

```json
{
  "PacingStrategies": {
    "Enabled": false,
    "SegmentSpecificGains": false,
    "NpIfTarget": false,
    "TimeTarget": false,
    "RpeZoneShift": false,
    "VariableMatchBurning": false,
    "MaximumDefinitionBytes": 65536,
    "MaximumRules": 10,
    "MaximumPhases": 10
  }
}
```

The capability response is the only source the client uses to decide which editors to show. Configuration is an availability gate, not a substitute for server validation.

**Step 3: Implement endpoint mapping**

Keep the new mapper separate from `PredictionEndpoints`. Catch only known service exceptions and map them through `ApiProblems`; allow cancellation and unknown failures to follow existing middleware behavior.

**Step 4: Add client methods**

Add typed list/detail/create/delete/capability methods and fake-client recording collections. Do not add adjustment fields to `PredictionDetailResponse`; the page loads the child collection separately.

**Step 5: Run API and client API tests, then commit**

```bash
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore --filter FullyQualifiedName~PredictionAdjustmentEndpoint -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Client.Tests/RouteTimer.Client.Tests.csproj --no-restore --filter FullyQualifiedName~RouteTimerApiClient -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git add src/RouteTimer.Api src/RouteTimer.Client/Api tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs tests/RouteTimer.Client.Tests/Api tests/RouteTimer.Client.Tests/Fakes
git commit -m "feat: expose prediction adjustment APIs"
```

**Step 6: Push and summarize**

```bash
git push
```

Summarize the change for this task: the five new routes, the capability/feature-flag contract, ownership-leak protection, and client method coverage. This is a review checkpoint (shared resource contract) — flag anything about the Problem Details mapping or capability gating a reviewer should double-check.

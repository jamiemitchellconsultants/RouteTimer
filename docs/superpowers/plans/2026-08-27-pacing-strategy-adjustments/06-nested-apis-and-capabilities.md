[← Back to plan overview](README.md)

# Task 6: Expose nested APIs and capabilities

**Files:**

- Create: `src/RouteTimer.Contracts/Adjustments/PredictionAdjustmentContracts.cs` (moved here from [Task 3](03-adjustment-domain-contracts.md); see that task's implementation note)
- Create: `src/RouteTimer.Api/Adjustments/PacingStrategyOptions.cs`
- Create: `src/RouteTimer.Api/Endpoints/PredictionAdjustmentEndpoints.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Modify: `src/RouteTimer.Api/appsettings.json`
- Modify: `src/RouteTimer.Api/appsettings.Development.json`
- Modify: `src/RouteTimer.Client/Api/IRouteTimerApiClient.cs`
- Modify: `src/RouteTimer.Client/Api/RouteTimerApiClient.cs`
- Modify: `tests/RouteTimer.Client.Tests/Fakes/FakeRouteTimerApiClient.cs`
- Test: `tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs`
- Modify: `tests/RouteTimer.Client.Tests/Api/RouteTimerApiClientTests.cs`

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

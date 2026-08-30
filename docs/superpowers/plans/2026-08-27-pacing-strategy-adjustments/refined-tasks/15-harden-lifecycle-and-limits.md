[← Refined task index](README.md)

# Task 15: Harden lifecycle, limits, and backward compatibility

**Method:** Add adversarial tests first. Several safeguards already exist after Tasks 1–8; preserve them
and make only the production changes named below. This task does not redesign states or add a sibling cap.

## Files

- Create: `tests/RouteTimer.Services.Tests/Adjustments/PredictionAdjustmentFailureTests.cs`
- Modify: `src/RouteTimer.Services/Adjustments/PacingStrategyJson.cs`
- Modify: `src/RouteTimer.Services/Adjustments/PredictionAdjustmentJobHandler.cs` to validate annotation
  values before constructing publication rows; do not add strategy switches.
- Modify: `src/RouteTimer.Persistence/Repositories/PredictionRepository.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/PredictionAdjustmentRepository.cs`
- Modify: `tests/RouteTimer.Services.Tests/Adjustments/PredictionAdjustmentWorkflowTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Predictions/PredictionDeletionServiceTests.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/PredictionAdjustmentRepositoryTests.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/PredictionRepositoryTests.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/PostgresMigrationTests.cs`
- Modify: `tests/RouteTimer.Api.Tests/Endpoints/PredictionAdjustmentEndpointTests.cs`
- Modify: existing baseline endpoint snapshot/shape tests in
  `tests/RouteTimer.Api.Tests/Endpoints/PredictionEndpointTests.cs`.

Do not add a new configuration system or edit default feature flags. `PacingStrategies` values in
`src/RouteTimer.Api/appsettings.json` must remain false/65,536/10/10.

## Required state outcomes

| Scenario | Required outcome |
| --- | --- |
| parallel sibling creation | distinct adjustment/job IDs; both children remain |
| duplicate delivery/same worker | first publish succeeds, second returns false; no duplicate segments |
| stale worker after lease loss | publish returns false; current owner/state unchanged |
| delete queued child | active job becomes Cancelled; child is deleted; baseline/siblings remain |
| delete running child | job becomes Cancelled and loses worker/lease; stale publish returns false |
| delete baseline with active children | baseline job and every active adjustment job become Cancelled; baseline, child, and segments cascade |
| cancellation during search | `OperationCanceledException` escapes strategy/handler; no publication |
| all candidates invalid | permanent job diagnostic `invalid-prediction-adjustment-result`; no result rows |
| malformed/unknown stored strategy | permanent `invalid-prediction-adjustment-strategy`; no publication |
| unknown adjustment warning | permanent `invalid-prediction-adjustment-result`; baseline warnings unchanged |
| cross-baseline detail/delete | `404 adjustment-not-found`; no existence leak |
| pre-feature succeeded baseline | adjustment reconstructs from persisted segments and succeeds |

## Exact resource-bound changes

`PacingStrategyJson.Deserialize<T>` currently checks JSON structure but not stored UTF-8 byte length and
only translates `JsonException`. Change it to:

1. reject over `MaximumBytes` before deserializing with `PacingStrategyValidationException` code
   `pacing-strategy-too-large`;
2. translate `JsonException`, `NotSupportedException`, and constructor-thrown `ArgumentException` to
   `pacing-strategy-invalid`;
3. do not catch `OperationCanceledException`, `OutOfMemoryException`, or arbitrary exceptions;
4. retain constructor validation as the second worker-side list/range checkpoint.

API request mapping must handle null collection properties produced by malformed JSON as validation
problems, not `NullReferenceException`. Update each strategy mapper to treat null rules/assignments/windows
as an empty invalid collection and key the error to `rules`, `assignments`, or `windows`.

The 65,536-byte boundary is canonical domain JSON at creation and stored UTF-8 bytes at worker read. In
`PacingStrategyJsonTests`, use a test-only concrete definition carrying a string payload to exercise ASCII
JSON at/below and above the limit; include a multibyte payload proving UTF-8 count is authoritative. Do
not try to inflate an API strategy with unknown fields: request-to-domain mapping intentionally discards
unknown wire fields before canonicalization.

## Exact baseline-deletion correction

`PredictionRepository.DeleteAsync` currently locks/cancels only the baseline's `PredictRoute` job. Extend
the same transaction:

1. lock the baseline row;
2. query/lock all `prediction_adjustments.Id` for that prediction;
3. query/lock queued/running `AdjustPrediction` jobs whose `SubjectId` is in those IDs;
4. cancel baseline and adjustment jobs with the same fields already used for baseline cancellation;
5. delete the prediction and let FK cascades remove children/segments;
6. commit only after upload reference cleanup.

Use explicit IDs derived under the transaction; do not issue an unconstrained job update. Keep completed
jobs unchanged for audit history. For EF InMemory tests mirror the same predicates without raw SQL.

## Checkpoint 15.1: Worker deserialization and calculation failures

- [ ] Add failure tests for malformed JSON, unknown enum/discriminator stored for a concrete handler,
  oversized stored JSON, constructor-invalid collections/ranges, all-search-candidates invalid,
  cancellation, unknown warning, sequence mismatch, and a non-finite annotation value.
- [ ] Pin worker-side byte validation before parsing:

```csharp
[Fact]
public void Deserialize_rejects_stored_json_above_the_utf8_limit_before_parsing()
{
    var oversized = new string('x', PacingStrategyJson.MaximumBytes + 1);

    var exception = Assert.Throws<PacingStrategyValidationException>(() =>
        PacingStrategyJson.Deserialize<SegmentGainsDefinition>(oversized));

    Assert.Equal("pacing-strategy-too-large", exception.Code);
}
```

- [ ] Assert exact `PredictionAdjustmentJobException.Code` and zero publish calls. Cancellation asserts
  `OperationCanceledException`, not a stored permanent diagnostic at the handler boundary.
- [ ] Implement the `PacingStrategyJson` change and only the minimal job-boundary validation exposed by
  tests. `PredictionAdjustmentAnnotation.WPrimeBalanceJoules` must be null or finite/non-negative;
  `ZoneNumber` must be null or positive; `StrategyPhase` must be null or one of
  `baseline/conservation/recovery/burn` when supplied.
- [ ] Run:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionAdjustmentFailure|FullyQualifiedName~PredictionAdjustmentWorkflow|FullyQualifiedName~PacingStrategyJson" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

- [ ] Commit: `fix: validate persisted pacing adjustments`.

## Checkpoint 15.2: Concurrent ownership and deletion

- [ ] PostgreSQL-backed tests cover the state table above, including two repository contexts/tasks for
  sibling creation and stale publication. Use bounded `Task.WhenAll`; do not assert wall-clock ordering.
- [ ] Add the baseline-delete test with two children (one queued, one running), child segments, and a
  completed sibling job. Assert active jobs Cancelled, completed job unchanged, child data removed, and
  no unrelated baseline/job touched.
- [ ] Implement baseline-deletion correction. Preserve existing adjustment child-delete transaction.
- [ ] Run:

```bash
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionAdjustmentRepository|FullyQualifiedName~PredictionRepository" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~PredictionDeletionService" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

- [ ] Commit: `fix: cancel adjustment jobs with baseline deletion`.

## Checkpoint 15.3: API limits and compatibility

- [ ] Endpoint tests cover null collections, every strategy's 11-item limit, exact numeric bounds,
  cross-baseline probing, disabled strategies, and stored-result reads
  after a strategy flag is disabled.
- [ ] Baseline contract tests serialize `PredictionSubmissionResponse`, `PredictionSummaryResponse`, and
  `PredictionDetailResponse` and assert no property named `strategy`, `adjustment`, `adjustments`,
  `adjustmentId`, or adjusted export selector appears.
- [ ] Add a PostgreSQL migration-chain test that inserts a succeeded baseline using the schema immediately
  before `AddPredictionAdjustments`, migrates to latest, then creates/processes an adjustment from its
  retained segments. Do not reparse GPX.
- [ ] Run all affected projects:

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Api.Tests/RouteTimer.Api.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] Confirm with `git diff -- src/RouteTimer.Api/appsettings.json` that no flag/default changed.
- [ ] Commit and push:

```bash
git add src tests
git commit -m "test: harden pacing adjustment lifecycle"
git push
```

## Task 15 acceptance

- Validation occurs at client convenience, API/domain authority, canonical byte boundary, and worker
  deserialization; only server/domain/worker checks are security boundaries.
- There is no sibling-retention cap and no mutation of completed children.
- Deletion serializes against publication and cancels every active owned job.
- Baseline request/response/export contracts remain adjustment-free.

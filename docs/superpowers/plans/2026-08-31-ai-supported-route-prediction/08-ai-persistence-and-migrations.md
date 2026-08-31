[← Plan overview](README.md)

# AI Persistence and Migrations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist rebuildable derived examples and immutable published/rejected AI evaluations with strict artifact validation and atomic current-model replacement.

**Architecture:** A composite-key example cache records the chronological prefix digest. AI evaluations store application-owned structured JSON; only a passing publication transaction moves the singleton current pointer, while rejected evaluations leave the prior current model untouched.

**Tech Stack:** EF Core 10/Npgsql/PostgreSQL JSONB and bytea, System.Text.Json, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Training activities/weather remain authoritative; example rows are disposable caches.
- Unknown enums, versions, JSON members, non-finite values, malformed arrays, and invalid bounds are rejected on read.
- Rejected evaluation persistence never clears the current published model.
- Published model replacement and old-current demotion occur in one transaction.

### Task 8: Add AI repositories, entities, validation and migration

**Files:**

- Modify: `src/RouteTimer.Domain/Ai/AiModelArtifacts.cs`
- Create: `src/RouteTimer.Services/Persistence/IAiTrainingExampleRepository.cs`
- Create: `src/RouteTimer.Services/Persistence/IRiderAiModelRepository.cs`
- Create: `src/RouteTimer.Services/Ai/Models/AiArtifactJson.cs`
- Create: `src/RouteTimer.Persistence/Entities/AiTrainingExampleEntity.cs`
- Create: `src/RouteTimer.Persistence/Entities/RiderAiModelEntity.cs`
- Create: `src/RouteTimer.Persistence/Repositories/AiTrainingExampleRepository.cs`
- Create: `src/RouteTimer.Persistence/Repositories/RiderAiModelRepository.cs`
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Create: `src/RouteTimer.Persistence/Migrations/*_AddRiderAiModels.cs` with generated designer/snapshot changes
- Create: `tests/RouteTimer.Services.Tests/Ai/Models/AiArtifactJsonTests.cs`
- Create: `tests/RouteTimer.Persistence.Tests/AiTrainingExampleRepositoryTests.cs`
- Create: `tests/RouteTimer.Persistence.Tests/RiderAiModelRepositoryTests.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/PostgresMigrationTests.cs`

**Interfaces:**

```csharp
public sealed record AiTrainingExampleRecord(
    Guid ActivityId,
    DateTimeOffset StartedAt,
    string FeatureSchemaVersion,
    string ReplayVersion,
    byte[] PrefixDigest,
    AiFeatureVector TypicalFeatures,
    AiTrainingState TrainingState,
    double? Multiplier,
    double? LogMultiplier,
    double? BaselineSeconds,
    double? ActualSeconds,
    string? ExclusionReason,
    DateTimeOffset CreatedAt);

public interface IAiTrainingExampleRepository
{
    Task<AiTrainingExampleRecord?> GetAsync(
        Guid activityId, string featureVersion, string replayVersion,
        byte[] prefixDigest, CancellationToken cancellationToken);
    Task UpsertAsync(AiTrainingExampleRecord record, CancellationToken cancellationToken);
}

public sealed record RiderAiModelSaveRequest(
    Guid TrainingRiderModelId,
    string CompatibleRiderModelAlgorithmVersion,
    RiderProfile ProfileSnapshot,
    AiReadinessSnapshot Readiness,
    AiChallengerEvaluation Evaluation,
    DateTimeOffset TrainingStartedAt,
    DateTimeOffset TrainingEndedAt,
    DateTimeOffset CreatedAt);

public interface IRiderAiModelRepository
{
    Task<Guid> SaveEvaluationAsync(RiderAiModelSaveRequest request, CancellationToken cancellationToken);
    Task<RiderAiModelSnapshot?> GetCurrentAsync(CancellationToken cancellationToken);
    Task<RiderAiModelSnapshot?> GetCurrentCompatibleAsync(
        string riderModelAlgorithmVersion, RiderProfile profile, CancellationToken cancellationToken);
    Task<RiderAiModelSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<RiderAiModelSnapshot?> GetLatestEvaluationAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing canonical-JSON tests**

Round-trip valid Typical-only and Typical+Today snapshots. Assert fixed camelCase, no named floating-point literals, no comments/trailing commas, exact version checks, no ignored unknown properties, and rejection of NaN/infinity, coefficient/count mismatch, invalid smooth terms, invalid support/ranges, Published without required artifacts/metrics, Rejected with artifacts, and empty serving-bound intersection. Rejected evaluations may persist null validation metrics when too few folds exist.

- [ ] **Step 2: Implement strict artifact JSON**

Use dedicated persistence DTOs and explicit mapping rather than serializing domain objects directly. After deserialization, reconstruct validated domain records. Cap each artifact JSON field at 1 MiB by UTF-8 byte count before database write and after database read; throw `InvalidPersistedAiModelException` on violation.

- [ ] **Step 3: Write failing example-repository tests**

Cover composite-key hit/miss, defensive digest copy, same activity/version with a different prefix digest creating a distinct row, successful and excluded examples, upsert idempotency, activity deletion cascade for the target row, malformed JSON/read rejection, and stable UTC timestamps.

- [ ] **Step 4: Implement example entity/repository**

Use composite key `(ActivityId, FeatureSchemaVersion, ReplayVersion, PrefixDigest)` with the digest as 32-byte `bytea`; JSONB for Typical/Today vectors; nullable label/baseline/actual fields; exclusion reason max length 128; and FK to training activity with cascade delete. Require either all four success numeric fields and no exclusion, or no success fields plus an exclusion.

- [ ] **Step 5: Write failing AI-model repository tests**

Assert first Published becomes current, second Published atomically demotes first, Rejected remains non-current and leaves prior current, latest evaluation returns Rejected even when current is older Published, compatibility requires exact deterministic algorithm and exact profile double values, `GetAsync` retrieves historical versions, deletion is not exposed, and corrupt DB data throws.

- [ ] **Step 6: Implement model entity/repository**

Store scalar metadata plus separate JSONB fields for readiness, Typical, Today, route support, state support, and metrics. Use `IsCurrent` with a PostgreSQL unique partial index where true. FK `TrainingRiderModelId` uses Restrict; predictions add their FK in Task 10. Save a rejected row with `IsCurrent=false`. In a Published transaction, insert the new row, set any current row false, set new true, and save once before commit.

- [ ] **Step 7: Generate and verify migration**

```bash
dotnet ef migrations add AddRiderAiModels --project src/RouteTimer.Persistence --startup-project src/RouteTimer.Api
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter FullyQualifiedName~PostgresMigrationTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Inspect generated SQL/model snapshot for composite bytea key, JSONB columns, both Restrict/Cascade relationships, and current partial index.

- [ ] **Step 8: Run focused persistence tests**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter FullyQualifiedName~AiArtifactJsonTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~AiTrainingExampleRepositoryTests|FullyQualifiedName~RiderAiModelRepositoryTests|FullyQualifiedName~PostgresMigrationTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 9: Commit and push**

```bash
git add src/RouteTimer.Domain/Ai src/RouteTimer.Services/Persistence src/RouteTimer.Services/Ai/Models src/RouteTimer.Persistence tests/RouteTimer.Services.Tests/Ai/Models tests/RouteTimer.Persistence.Tests
git commit -m "feat: persist AI training and model artifacts"
git push
git status --short
```

Expected: successful push and empty status.

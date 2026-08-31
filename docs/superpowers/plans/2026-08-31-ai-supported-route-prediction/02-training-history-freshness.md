[← Plan overview](README.md)

# Training History Freshness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist how recently the rider confirmed a complete activity history and make Garmin checks advance that marker even when no rides are returned.

**Architecture:** A singleton database row records `confirmed_through` and source. A service owns monotonic server-time updates and the 48-hour freshness rule; Garmin activity-list success calls the same repository only after a valid response.

**Tech Stack:** Domain records, EF Core 10/Npgsql, PostgreSQL migration, existing Garmin service, xUnit and Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Never accept a client-supplied confirmation timestamp.
- Failed, cancelled, reconnect-required, or invalid Garmin responses do not advance confirmation.
- Confirmation is monotonic; an older completion cannot move the marker backward.
- This task adds no HTTP endpoint; Task 12 exposes manual confirmation.

### Task 2: Persist and update current-history confirmation

**Files:**

- Modify: `src/RouteTimer.Domain/Ai/AiReadiness.cs`
- Create: `src/RouteTimer.Services/Persistence/ITrainingHistoryStateRepository.cs`
- Create: `src/RouteTimer.Services/Ai/Readiness/TrainingHistoryFreshnessService.cs`
- Create: `src/RouteTimer.Persistence/Entities/TrainingHistoryStateEntity.cs`
- Create: `src/RouteTimer.Persistence/Repositories/TrainingHistoryStateRepository.cs`
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Create: `src/RouteTimer.Persistence/Migrations/*_AddTrainingHistoryState.cs` with generated designer/snapshot changes
- Modify: `src/RouteTimer.Services/Garmin/GarminActivityService.cs`
- Modify: `src/RouteTimer.Api/Program.cs`
- Create: `tests/RouteTimer.Services.Tests/Ai/Readiness/TrainingHistoryFreshnessServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Garmin/GarminActivityServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Garmin/GarminImportServiceTests.cs`
- Create: `tests/RouteTimer.Persistence.Tests/TrainingHistoryStateRepositoryTests.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/PostgresMigrationTests.cs`

**Interfaces:**

```csharp
public sealed record TrainingHistoryConfirmation(
    DateTimeOffset ConfirmedThrough,
    TrainingHistoryConfirmationSource Source);

public interface ITrainingHistoryStateRepository
{
    Task<TrainingHistoryConfirmation?> GetAsync(CancellationToken cancellationToken);
    Task<TrainingHistoryConfirmation> ConfirmAsync(
        DateTimeOffset confirmedThrough,
        TrainingHistoryConfirmationSource source,
        CancellationToken cancellationToken);
}

public sealed record TrainingHistoryFreshness(
    bool IsCurrent,
    DateTimeOffset? ConfirmedThrough,
    TrainingHistoryConfirmationSource? Source,
    string? ReasonCode);

public sealed class TrainingHistoryFreshnessService(
    ITrainingHistoryStateRepository repository,
    TimeProvider timeProvider)
{
    public Task<TrainingHistoryConfirmation> ConfirmManualAsync(CancellationToken cancellationToken);
    public Task<TrainingHistoryConfirmation> ConfirmGarminAsync(
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);
    public Task<TrainingHistoryFreshness> GetAsync(
        DateTimeOffset predictionAt,
        CancellationToken cancellationToken);
}
```

The only stale reason is `today-history-stale`. Fresh means `predictionAt - ConfirmedThrough <= 48 hours`, with future confirmation treated as invalid persisted data.

- [ ] **Step 1: Write failing persistence tests**

Assert no row returns null, first confirmation creates singleton ID 1, later confirmation updates it, older confirmation leaves the newer value/source untouched, timestamps are UTC, and an unknown stored source throws `InvalidPersistedTrainingHistoryStateException`.

Run:

```bash
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter FullyQualifiedName~TrainingHistoryStateRepositoryTests -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: FAIL because the entity and repository do not exist.

- [ ] **Step 2: Add the singleton entity, mapping, repository and migration**

Map table `training_history_state` with `Id = 1` check constraint, `ConfirmedThrough` as `timestamp with time zone`, and `Source` max length 32. Use one transaction-safe PostgreSQL upsert pattern; for the in-memory provider, load/update normally. Parse enums canonically and reject invalid values at the service/repository boundary.

Generate the migration using:

```bash
dotnet ef migrations add AddTrainingHistoryState --project src/RouteTimer.Persistence --startup-project src/RouteTimer.Api
```

- [ ] **Step 3: Write failing freshness-service tests**

Use `FakeTimeProvider`. Cover absent state, exactly 48 hours, one tick beyond 48 hours, manual confirmation using server now, Garmin confirmation using supplied successful completion, monotonic repository results, and a persisted future timestamp becoming `InvalidPersistedTrainingHistoryStateException` rather than fresh.

- [ ] **Step 4: Implement freshness service**

Normalize all times to UTC. `ConfirmManualAsync` always uses `timeProvider.GetUtcNow()`. `ConfirmGarminAsync` accepts only a UTC instant no later than current server time; the caller passes the time captured after a successful adapter operation.

- [ ] **Step 5: Write failing Garmin integration tests**

Update both Garmin test fixture constructors with `ITrainingHistoryStateRepository`. Assert a valid activity page advances confirmation even when empty and every later page also advances it. Assert adapter failure, invalid token JSON, cancellation, linked-ID lookup failure, and authentication failure do not call the repository.

- [ ] **Step 6: Integrate Garmin success and DI**

Capture `timeProvider.GetUtcNow()` only after token validation/rotation, activity filtering, and linked-ID lookup succeed, immediately before returning `GarminActivityPage`. Call `ConfirmAsync(..., GarminCheck, token)`. A confirmation database failure fails the page request; do not claim history is current when the marker was not saved. Register repository and service in `Program.cs`.

- [ ] **Step 7: Run focused, migration and Garmin regressions**

```bash
dotnet test tests/RouteTimer.Services.Tests/RouteTimer.Services.Tests.csproj --no-restore --filter "FullyQualifiedName~TrainingHistoryFreshnessServiceTests|FullyQualifiedName~GarminActivityServiceTests|FullyQualifiedName~GarminImportServiceTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~TrainingHistoryStateRepositoryTests|FullyQualifiedName~PostgresMigrationTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

- [ ] **Step 8: Commit and push**

```bash
git add src/RouteTimer.Domain/Ai src/RouteTimer.Services/Persistence src/RouteTimer.Services/Ai/Readiness src/RouteTimer.Services/Garmin src/RouteTimer.Persistence src/RouteTimer.Api/Program.cs tests
git commit -m "feat: track current training history"
git push
git status --short
```

Expected: successful push and empty status.

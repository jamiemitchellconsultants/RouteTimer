[← Plan overview](README.md)

# Weather Persistence and Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist activity weather state, summaries, and atomic route-anchor observations with an additive PostgreSQL migration.

**Architecture:** Training activities own weather rows through cascade deletion. A dedicated repository handles enrichment state and atomic replacement, while the activity repository exposes identified model evidence and weather counts.

**Tech Stack:** EF Core 10, Npgsql/PostgreSQL, Testcontainers, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-31-weather-aware-training-and-download-design.md`

## Global Constraints

- Follow `README.md` Global Constraints and the Task 01 types.
- Existing activity rows migrate to `Pending`; existing sample bytes/values are unchanged.
- Never expose raw provider payloads or request URLs through persistence diagnostics.
- Replacement of one activity's observations and state is one transaction.

### Task 2: Add weather entities, repository, and migration

**Files:**

- Create: `src/RouteTimer.Persistence/Entities/ActivityWeatherObservationEntity.cs`
- Create: `src/RouteTimer.Persistence/Repositories/TrainingWeatherRepository.cs`
- Create: `src/RouteTimer.Services/Persistence/ITrainingWeatherRepository.cs`
- Modify: `src/RouteTimer.Persistence/Entities/TrainingActivityEntity.cs`
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Modify: `src/RouteTimer.Persistence/Repositories/TrainingActivityRepository.cs`
- Modify: `src/RouteTimer.Services/Persistence/ITrainingActivityRepository.cs`
- Create: `src/RouteTimer.Persistence/Migrations/20260831190000_AddTrainingWeather.cs`
- Create: `src/RouteTimer.Persistence/Migrations/20260831190000_AddTrainingWeather.Designer.cs`
- Modify: `src/RouteTimer.Persistence/Migrations/RouteTimerDbContextModelSnapshot.cs`
- Create: `tests/RouteTimer.Persistence.Tests/TrainingWeatherRepositoryTests.cs`
- Modify: `tests/RouteTimer.Persistence.Tests/PostgresMigrationTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/BuildModelJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/ModelRebuildServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Models/ModelStatusServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Training/ParseTrainingJobHandlerTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Training/TrainingActivityDeletionServiceTests.cs`
- Modify: `tests/RouteTimer.Services.Tests/Training/TrainingActivityQueryServiceTests.cs`

**Interfaces:**

```csharp
public sealed record TrainingWeatherSummary(
    double MinimumTemperatureCelsius,
    double MaximumTemperatureCelsius,
    double MaximumWindSpeedMetresPerSecond,
    double PrevailingWindDirectionDegrees,
    double PrecipitationTotalMillimetres);

public sealed record TrainingWeatherCounts(int ReadyEligible, int PendingEligible, int ExcludedEligible);

public sealed record TrainingWeatherSource(
    Guid ActivityId,
    CleanedActivity Activity,
    WeatherEnrichmentState State,
    string? ProviderVersion);

public interface ITrainingWeatherRepository
{
    Task<TrainingWeatherSource?> GetSourceAsync(Guid activityId, CancellationToken cancellationToken);
    Task SaveReadyAsync(Guid activityId, string providerVersion,
        IReadOnlyList<RouteWeatherObservation> observations,
        TrainingWeatherSummary summary, DateTimeOffset completedAt,
        CancellationToken cancellationToken);
    Task MarkFailedAsync(Guid activityId, string code, DateTimeOffset completedAt, CancellationToken cancellationToken);
    Task MarkUnavailableAsync(Guid activityId, string code, DateTimeOffset completedAt, CancellationToken cancellationToken);
    Task MarkPendingAsync(Guid activityId, DateTimeOffset requestedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> GetNeedingEnrichmentAsync(string providerVersion, int limit, CancellationToken cancellationToken);
}
```

Add to `ITrainingActivityRepository`:

```csharp
Task<IReadOnlyList<TrainingActivityModelEvidence>> GetModelEvidenceAsync(CancellationToken cancellationToken);
Task<TrainingWeatherCounts> GetWeatherCountsAsync(CancellationToken cancellationToken);
```

- [ ] **Step 1: Write failing EF model and repository tests**

Test `SaveAsync` starts a locatable ride in `Pending`; atomic `SaveReadyAsync` replaces rows rather than appending; summary/provenance round-trip; failed/unavailable diagnostics round-trip; `GetModelEvidenceAsync` returns activity IDs, states, and ordered observations; `GetNeedingEnrichmentAsync` returns Pending, Failed, and stale-version rows but not current Ready/Unavailable rows; deletion cascades observations.

Use SQLite for transaction/cascade behavior and PostgreSQL for schema/provider-specific assertions. Expected initial failure: missing entity/repository members.

- [ ] **Step 2: Add entity properties and EF mapping**

`TrainingActivityEntity` gains state/provenance/request/completion/diagnostic and nullable summary properties plus `List<ActivityWeatherObservationEntity> WeatherObservations`. Configure max lengths (`32` for state, `128` for provider/code), UTC timestamp columns, composite observation key `(ActivityId, AnchorSequence, ValidAt)`, unique index with that key, and cascade relationship.

The observation entity mirrors every field in `RouteWeatherObservation`; use doubles and UTC timestamps. Map table name `activity_weather_observations`.

- [ ] **Step 3: Implement repository state transitions**

`SaveReadyAsync` must load the activity plus observations, begin a relational transaction, validate a non-empty rectangular set, remove old rows, insert new rows, set summary/provenance/state, clear diagnostics, save, and commit. On cancellation/exception, no partial replacement is visible.

Compute no summary inside persistence; accept the service-computed summary. `MarkFailedAsync` and `MarkUnavailableAsync` clear observations and summary so stale Ready data cannot be mistaken for current evidence. `MarkPendingAsync` retains no observations.

- [ ] **Step 4: Implement identified model evidence and counts**

Load activities with samples and weather rows, map existing `CleanedActivity` exactly as today, and map observations ordered by anchor then time. Counts apply the existing `ActivityEligibility.Eligible` string plus weather state: Ready, Pending, and Excluded (`Failed` or `Unavailable`). Keep `GetAllAsync` for existing callers until Task 08 switches model orchestration.

- [ ] **Step 5: Generate the migration**

```bash
dotnet ef migrations add AddTrainingWeather --project src/RouteTimer.Persistence --startup-project src/RouteTimer.Persistence
```

Normalize the generated migration ID to `20260831190000_AddTrainingWeather`: rename both generated files and update the `[Migration("...")]` attribute in the designer before building. Inspect generated code. It must add the activity columns with existing rows defaulted to `Pending`, create the observation table and cascade FK, create the unique key/index, and contain a reversible `Down`.

- [ ] **Step 6: Extend PostgreSQL migration assertions**

Update the expected table list to include `activity_weather_observations`. Query `information_schema.columns`, PostgreSQL constraints, and the migrated legacy row to prove state is `Pending`, key columns exist, timestamps use `timestamp with time zone`, and cascade deletion works.

- [ ] **Step 7: Run focused and persistence suites**

```bash
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~TrainingWeatherRepositoryTests|FullyQualifiedName~PostgresMigrationTests" -p:UseSharedCompilation=false -m:1 -nodeReuse:false
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
git diff --check
```

Expected: all tests pass. If Testcontainers cannot start, record the exact infrastructure failure, rerun once, and do not claim schema verification without a successful PostgreSQL run.

- [ ] **Step 8: Commit and push**

```bash
git add src/RouteTimer.Persistence src/RouteTimer.Services/Persistence tests/RouteTimer.Persistence.Tests tests/RouteTimer.Services.Tests tests/RouteTimer.Api.Tests
git commit -m "feat: persist training weather evidence"
git push
git status --short
```

Expected: one commit, successful push, empty status.

[← Back to plan overview](README.md)

# Task 4: Persist adjustment aggregates and enforce ownership

**Files:**

- Create: `src/RouteTimer.Persistence/Entities/PredictionAdjustmentEntity.cs`
- Create: `src/RouteTimer.Persistence/Entities/PredictionAdjustmentSegmentEntity.cs`
- Create: `src/RouteTimer.Services/Persistence/IPredictionAdjustmentRepository.cs`
- Create: `src/RouteTimer.Persistence/Repositories/PredictionAdjustmentRepository.cs`
- ~~Modify: `src/RouteTimer.Persistence/Entities/PredictionEntity.cs`~~ not needed (see note below)
- Modify: `src/RouteTimer.Persistence/RouteTimerDbContext.cs`
- Create: `src/RouteTimer.Persistence/Migrations/*_AddPredictionAdjustments.cs`
- Create: `src/RouteTimer.Persistence/Migrations/*_AddPredictionAdjustments.Designer.cs`
- Modify: `src/RouteTimer.Persistence/Migrations/RouteTimerDbContextModelSnapshot.cs`
- Test: `tests/RouteTimer.Persistence.Tests/PredictionAdjustmentRepositoryTests.cs`
- ~~Modify: `tests/RouteTimer.Persistence.Tests/PostgresMigrationTests.cs`~~ not needed (see note below)

**Implementation notes (deviations from plan):**

- **No `PredictionEntity.Adjustments` navigation.** The `PredictionId` foreign key is configured as a
  shadow relationship (`HasOne<PredictionEntity>().WithMany()` with no collection property on
  `PredictionEntity`), matching how nothing currently needs to eager-load a baseline's adjustments
  from the baseline side — the adjustment repository always queries `prediction_adjustments` directly
  by `PredictionId`. Adding an unused navigation property would be speculative.
- **`PostgresMigrationTests.cs`'s fixed-table-list test is untouched.** That test
  (`Migrate_creates_the_stored_uploads_table_on_a_fresh_database`) asserts a fixed historical subset of
  tables (it doesn't include `prediction_segments`, `analysis_jobs`, or `google_maps_credentials`
  either) — it's checking that the full migration history still applies cleanly to a fresh database,
  not maintaining an exhaustive table inventory. `Model_has_no_pending_changes` (already in that same
  file) is what actually guards the new tables against model/snapshot drift.
- **Job creation and enqueueing are not in this repository.** `CreateQueuedAsync` here inserts only the
  `prediction_adjustments` row and returns `AdjustmentBaselineStatus` plus the new adjustment's id — no
  `AnalysisJobEntity` row is created, unlike `PredictionRepository.CreateQueuedAsync`'s baseline
  pattern (which creates prediction and job together in one transaction). Task 5's Step 1 explicitly
  tests "cleanup if enqueue fails," which only makes sense if enqueueing is a separate, independently
  failable step after the insert — so the codebase's existing generic `IJobQueue.EnqueueAsync` (already
  used by `ModelRebuildService` and `ParseTrainingJobHandler` for the same decoupled-enqueue pattern) is
  the right fit, wired up in Task 5's `PredictionAdjustmentService`, not here.
- **`"AdjustPrediction"` is a literal string in this repository**, not `JobType.AdjustPrediction`,
  since that enum member doesn't exist until Task 5 modifies `AnalysisJob.cs`. It stringifies to the
  same value the repository already writes, matching how `PredictionRepository` also mixes literal
  `"PredictRoute"` strings with `JobType.PredictRoute.ToString()`.
- **Unknown-warning validation is not in this repository**, matching precedent:
  `PredictionJobHandler.BuildPublication` validates warning codes against `PredictionWarningCodes`
  *before* calling `IPredictionRepository.TryPublishAsync`, not inside the repository. Task 5's
  `PredictionAdjustmentJobHandler` will do the same against `AdjustmentWarningCodes` before calling
  `TryPublishAsync` here. This repository does still validate the sequence-set match itself (rejecting
  publication when the adjusted segment sequences don't exactly match the baseline's), since that check
  inherently needs the baseline's persisted segments and is a repository-level ownership concern.

**Step 1: Write failing repository tests**

Cover:

- create under a succeeded baseline and reject queued/running/failed/cancelled/missing baselines;
- list newest-first and fetch only when both baseline and adjustment IDs match;
- preserve canonical strategy JSON exactly;
- publish summary, report, warnings, annotations, and all segment values atomically;
- reject unknown warnings and sequence sets differing from the baseline;
- reject stale job/worker publication;
- delete one child without touching the baseline or sibling;
- cascade adjustment rows when the baseline is deleted; and
- round-trip through PostgreSQL, not only EF InMemory.

**Step 2: Map the schema**

Add `prediction_adjustments` and `prediction_adjustment_segments` exactly as specified. Use:

- FK `PredictionId -> predictions.Id ON DELETE CASCADE`;
- composite PK `(AdjustmentId, Sequence)` for child segments;
- unique index `(PredictionId, Id)` if needed for composite ownership joins;
- index `(PredictionId, CreatedAt DESC)`;
- max lengths for state, strategy type, version, confidence, and phase;
- `jsonb` for strategy, report, and warnings; and
- finite/range validation in repository publication before EF mutation.

Do not copy baseline geometry into adjusted rows. Query details by joining each adjusted sequence to the owning baseline segment.

**Step 3: Generate the migration with the repository's normal EF command**

```bash
dotnet ef migrations add AddPredictionAdjustments --project src/RouteTimer.Persistence --startup-project src/RouteTimer.Api
```

Inspect generated SQL semantics and ensure both cascades and indexes are present. Do not hand-edit the model snapshot.

**Step 4: Run persistence tests**

```bash
dotnet test tests/RouteTimer.Persistence.Tests/RouteTimer.Persistence.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 -nodeReuse:false
```

Expected: repository, PostgreSQL migration, and `Model_has_no_pending_changes` tests pass.

**Step 5: Commit**

```bash
git add src/RouteTimer.Persistence src/RouteTimer.Services/Persistence tests/RouteTimer.Persistence.Tests
git commit -m "feat: persist prediction adjustments"
```

**Step 6: Push and summarize**

```bash
git push
```

Summarize the change for this task: the new schema, cascades and indexes, ownership enforcement, and the PostgreSQL migration test evidence. Note anything about the generated migration a reviewer should verify against the model snapshot.

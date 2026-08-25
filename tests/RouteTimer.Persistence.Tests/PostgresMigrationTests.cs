using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace RouteTimer.Persistence.Tests;

public sealed class PostgresMigrationTests
{
    // Break caught: the repository appears correct under EF InMemory but does not commit or reconstruct all normalized children under PostgreSQL.
    [Fact]
    public async Task Rider_model_repository_round_trips_complete_immutable_model_through_PostgreSQL()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        var profile = new RiderProfile(73.5, 8.75);
        var validation = new ModelValidationSummary(ModelValidationStatus.Passed, .047, .091);
        var bands = new[]
        {
            new PowerBand("climb", "long", 215, TimeSpan.FromMinutes(50), 4, .7, ConfidenceLevel.Medium),
            new PowerBand("flat", "short", 260, TimeSpan.FromMinutes(35), 5, .85, ConfidenceLevel.High)
        };
        var cells = DescentLimitModel.Conservative.Cells
            .Select((cell, index) => cell with
            {
                SpeedCapMetresPerSecond = index == 8 ? .75 : 11 + index,
                Evidence = index == 0 ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(index == 8 ? 20 : 5 + index),
                ActivityCount = index == 0 ? 2 : index == 8 ? 3 : 2,
                Confidence = index == 0 ? ConfidenceLevel.Low : index == 8 ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                IsFallback = index == 0
            })
            .ToArray();
        var model = new RiderModel(
            new PowerModel(bands, 238),
            new PhysicalCoefficients(.966, 1.19, .0042, .305),
            new DescentLimitModel(cells),
            true,
            "sequential-v8");

        Guid id;
        await using (var saveContext = new RouteTimerDbContext(options))
        {
            await saveContext.Database.MigrateAsync();
            id = await new RiderModelRepository(saveContext).SaveAsync(model, profile, validation, CancellationToken.None);
        }

        await using var loadContext = new RouteTimerDbContext(options);
        var loaded = await new RiderModelRepository(loadContext).GetAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(profile, loaded.ProfileSnapshot);
        Assert.Equal(validation, loaded.Validation);
        Assert.Equal(model.WasCalibrated, loaded.WasCalibrated);
        Assert.Equal(model.AlgorithmVersion, loaded.Model.AlgorithmVersion);
        Assert.Equal(model.Coefficients, loaded.Model.Coefficients);
        Assert.Equal(model.PowerModel.GlobalTypicalWatts, loaded.Model.PowerModel.GlobalTypicalWatts);
        Assert.Equal(bands, loaded.Model.PowerModel.Bands);
        Assert.True(loaded.DescentWasLearned);
        Assert.Equal(cells, loaded.Model.DescentLimits.Cells);
        Assert.Equal(.75, loaded.Model.DescentLimits.Cells[^1].SpeedCapMetresPerSecond);
        Assert.Equal(9, await loadContext.RiderModelDescentLimits.CountAsync(cell => cell.ModelId == id));
        Assert.True(await loadContext.RiderModels.Where(entity => entity.Id == id).Select(entity => entity.DescentWasLearned).SingleAsync());
    }

    // Break caught: PostgreSQL rows can bypass save-time checks and reconstruct malformed aggregate parts without one permanent classification.
    [Fact]
    public async Task Rider_model_repository_rejects_corrupted_whole_model_rows_through_PostgreSQL()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await using (var migrationContext = new RouteTimerDbContext(options))
        {
            await migrationContext.Database.MigrateAsync();
        }

        var corruptions = new[] { "profile", "coefficients", "power-band", "validation-status-numeric", "descent" };
        foreach (var corruption in corruptions)
        {
            Guid id;
            await using (var saveContext = new RouteTimerDbContext(options))
            {
                var model = new RiderModel(
                    new PowerModel([new PowerBand("foreign-grade", "foreign-duration", 200, TimeSpan.FromMinutes(5), 2, .25, ConfidenceLevel.Medium)], 200),
                    PhysicalCoefficients.Default,
                    DescentLimitModel.Conservative,
                    false,
                    "legacy-v1");
                id = await new RiderModelRepository(saveContext).SaveAsync(
                    model,
                    new RiderProfile(75, 10),
                    new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08),
                    CancellationToken.None);
            }

            await using (var corruptContext = new RouteTimerDbContext(options))
            {
                var entity = await corruptContext.RiderModels
                    .Include(model => model.Bands)
                    .Include(model => model.DescentLimits)
                    .SingleAsync(model => model.Id == id);
                RiderModelRepositoryValidationTests.Corrupt(entity, corruption);
                await corruptContext.SaveChangesAsync();
            }

            await using var loadContext = new RouteTimerDbContext(options);
            await Assert.ThrowsAsync<RouteTimer.Services.Persistence.InvalidPersistedRiderModelException>(
                () => new RiderModelRepository(loadContext).GetAsync(id, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Migrate_creates_the_stored_uploads_table_on_a_fresh_database()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();

        var options = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .Options;
        await using var context = new RouteTimerDbContext(options);

        await context.Database.MigrateAsync();

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT array_agg(to_regclass(table_name)::text ORDER BY table_name) FROM (VALUES ('predictions'), ('rider_profile'), ('stored_uploads'), ('training_activities'), ('activity_samples'), ('rider_models'), ('power_bands'), ('rider_model_descent_limits')) AS expected_tables(table_name)";
        await context.Database.OpenConnectionAsync();
        var table = await command.ExecuteScalarAsync();

        Assert.Equal(new[] { "activity_samples", "power_bands", "predictions", "rider_model_descent_limits", "rider_models", "rider_profile", "stored_uploads", "training_activities" }, (string[]?)table);

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains("20260824200000_AddSequentialSimulationModel", applied);
    }

    // Break caught: upgrading a pre-step-8 database loses model data, omits fallback cells, or leaves old samples without deterministic curvature.
    [Fact]
    public async Task Sequential_simulation_migration_upgrades_legacy_models_and_supports_down_up_with_cascade()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260824184955_AddDurablePredictions");

        var modelId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO rider_models
                ("Id", "CreatedAt", "ProfileRiderWeightKg", "ProfileBikeWeightKg", "AlgorithmVersion",
                 "DrivetrainEfficiency", "AirDensity", "Crr", "CdA", "WasCalibrated", "GlobalTypicalWatts",
                 "ValidationStatus", "ValidationMedianApe", "ValidationP90Ape")
            VALUES
                ({modelId}, TIMESTAMPTZ '2026-08-24 18:00:00+00', 74.5, 9.25, 'legacy-v7',
                 .965, 1.2, .0045, .31, TRUE, 237,
                 'Passed', .081, .17);
            INSERT INTO power_bands
                ("ModelId", "GradeKey", "DurationKey", "TypicalWatts", "EvidenceSeconds", "ActivityCount", "ShrinkageWeight", "Confidence")
            VALUES ({modelId}, 'climb', 'long', 222, 1800, 4, .8, 'High');
            INSERT INTO training_activities
                ("Id", "UploadId", "Name", "MovingDurationSeconds", "Eligibility", "PositionCoverage", "ElevationCoverage",
                 "SpeedCoverage", "PowerCoverage", "ExclusionCounts", "ReasonCodes", "CreatedAt")
            VALUES
                ({activityId}, {Guid.NewGuid()}, 'Legacy ride', 1, 'Eligible', 1, 1, 1, 1, jsonb_build_object(), jsonb_build_array(),
                 TIMESTAMPTZ '2026-08-24 17:00:00+00');
            INSERT INTO activity_samples
                ("ActivityId", "Sequence", "Timestamp", "MovingElapsedSeconds", "Latitude", "Longitude", "ElevationMetres",
                 "SpeedMetresPerSecond", "PowerWatts", "HeartRate", "Cadence", "CrossesDiscontinuity", "Gradient")
            VALUES
                ({activityId}, 0, TIMESTAMPTZ '2026-08-24 17:00:00+00', 0, 51, -2, 100, 12, 180, 140, 85, FALSE, -.08);
            """);

        await migrator.MigrateAsync();

        context.ChangeTracker.Clear();
        var loaded = await new RiderModelRepository(context).GetAsync(modelId, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 18, 0, 0, TimeSpan.Zero), loaded.CreatedAt);
        Assert.Equal(new RouteTimer.Domain.Profile.RiderProfile(74.5, 9.25), loaded.ProfileSnapshot);
        Assert.Equal("legacy-v7", loaded.Model.AlgorithmVersion);
        Assert.Equal(237, loaded.Model.PowerModel.GlobalTypicalWatts);
        Assert.Equal(.965, loaded.Model.Coefficients.DrivetrainEfficiency);
        Assert.Equal(1.2, loaded.Model.Coefficients.AirDensity);
        Assert.Equal(.0045, loaded.Model.Coefficients.Crr);
        Assert.Equal(.31, loaded.Model.Coefficients.CdA);
        var band = Assert.Single(loaded.Model.PowerModel.Bands);
        Assert.Equal("climb", band.GradeKey);
        Assert.Equal("long", band.DurationKey);
        Assert.Equal(222, band.TypicalWatts);
        Assert.Equal(TimeSpan.FromMinutes(30), band.Evidence);
        Assert.Equal(4, band.ActivityCount);
        Assert.Equal(.8, band.ShrinkageWeight);
        Assert.Equal(RouteTimer.Domain.Models.ConfidenceLevel.High, band.Confidence);
        Assert.Equal(RouteTimer.Domain.Models.ModelValidationStatus.Passed, loaded.Validation.Status);
        Assert.Equal(.081, loaded.Validation.MedianAbsolutePercentageError);
        Assert.Equal(.17, loaded.Validation.P90AbsolutePercentageError);
        Assert.True(loaded.WasCalibrated);
        Assert.False(loaded.DescentWasLearned);
        Assert.Equal(9, loaded.Model.DescentLimits.Cells.Count);
        Assert.Equal(
            new[] { 13d, 13d, 13d, 16d, 16d, Math.Sqrt(200), 18d, 18d, Math.Sqrt(200) },
            loaded.Model.DescentLimits.Cells.Select(cell => cell.SpeedCapMetresPerSecond),
            new DoublePrecisionComparer(12));
        Assert.All(loaded.Model.DescentLimits.Cells, cell =>
        {
            Assert.Equal(TimeSpan.Zero, cell.Evidence);
            Assert.Equal(0, cell.ActivityCount);
            Assert.Equal(RouteTimer.Domain.Models.ConfidenceLevel.Low, cell.Confidence);
            Assert.True(cell.IsFallback);
        });

        var curvature = await ScalarAsync<double>(context, $"SELECT \"CurvaturePerMetre\" FROM activity_samples WHERE \"ActivityId\" = '{activityId}'");
        Assert.Equal(0, curvature);
        Assert.Equal(1L, await ScalarAsync<long>(context, "SELECT COUNT(*) FROM pg_constraint WHERE conname = 'FK_predictions_rider_models_RiderModelId'"));

        await migrator.MigrateAsync("20260824184955_AddDurablePredictions");
        Assert.Null(await ScalarAsync<string?>(context, "SELECT to_regclass('rider_model_descent_limits')::text"));
        await migrator.MigrateAsync();
        Assert.Equal(9L, await ScalarAsync<long>(context, $"SELECT COUNT(*) FROM rider_model_descent_limits WHERE \"ModelId\" = '{modelId}'"));

        await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM rider_models WHERE \"Id\" = {modelId}");
        Assert.Equal(0L, await ScalarAsync<long>(context, $"SELECT COUNT(*) FROM rider_model_descent_limits WHERE \"ModelId\" = '{modelId}'"));
    }

    // Break caught: legacy placeholder predictions cause a foreign-key failure partway through migration rather than a clear no-data-loss precondition.
    [Fact]
    public async Task Durable_prediction_migration_aborts_with_a_clear_error_when_legacy_prediction_rows_exist()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260824122226_AddActiveJobUniqueIndex");
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO predictions ("Id", "ModelVersion", "RiderWeightKg", "BikeWeightKg", "CreatedAt")
            VALUES ('11111111-1111-1111-1111-111111111111', 'legacy-preview', 75, 10, NOW());
            """);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync());

        Assert.Contains("legacy-predictions-not-supported", exception.ToString());
    }

    // Break caught: the Step 9 presentation-data migration must backfill legacy activity/job rows and replace the active-job partial index without losing queue semantics.
    [Fact]
    public async Task Presentation_data_migration_backfills_legacy_rows_and_splits_the_active_job_index()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260824200000_AddSequentialSimulationModel");

        var uploadId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 8, 24, 20, 15, 0, TimeSpan.Zero);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO stored_uploads ("Id", "Kind", "FileName", "Content", "Sha256", "CreatedAt")
            VALUES ({uploadId}, 'fit', 'legacy-ride.fit', decode('01020304', 'hex'), decode('00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff', 'hex'), {createdAt});

            INSERT INTO training_activities
                ("Id", "UploadId", "Name", "MovingDurationSeconds", "Eligibility", "PositionCoverage", "ElevationCoverage",
                 "SpeedCoverage", "PowerCoverage", "ExclusionCounts", "ReasonCodes", "CreatedAt")
            VALUES
                ({activityId}, {uploadId}, 'Legacy ride', 60, 'Eligible', 1, 1, 1, 1, jsonb_build_object(), jsonb_build_array(), {createdAt});

            INSERT INTO activity_samples
                ("ActivityId", "Sequence", "Timestamp", "MovingElapsedSeconds", "Latitude", "Longitude", "ElevationMetres",
                 "SpeedMetresPerSecond", "PowerWatts", "HeartRate", "Cadence", "CrossesDiscontinuity", "Gradient", "CurvaturePerMetre")
            VALUES
                ({activityId}, 0, {createdAt}, 0, 51, -2, 100, 10, 180, 140, 85, FALSE, 0, 0),
                ({activityId}, 1, {createdAt.AddMinutes(1)}, 60, 51.0001, -2.0001, 102, 11, 182, 141, 86, FALSE, .01, .001);

            INSERT INTO analysis_jobs
                ("Id", "Type", "SubjectId", "State", "AttemptCount", "WorkerId", "LeaseExpiresAt", "CreatedAt", "DiagnosticCode", "DiagnosticMessage")
            VALUES
                ({jobId}, 'BuildModel', {subjectId}, 'Queued', 0, NULL, NULL, {createdAt}, NULL, NULL);
            """);

        await migrator.MigrateAsync();
        context.ChangeTracker.Clear();

        var activity = await new TrainingActivityRepository(context).GetAsync(activityId, CancellationToken.None);
        var job = await new JobRepository(context).GetAsync(jobId, CancellationToken.None);

        Assert.NotNull(activity);
        Assert.Equal("legacy-ride.fit", activity!.Metadata.SourceFileName);
        Assert.Equal(createdAt, activity.Metadata.StartedAt);
        Assert.Equal(createdAt.AddMinutes(1), activity.Metadata.EndedAt);
        Assert.Null(activity.Metadata.DeviceManufacturer);
        Assert.Null(activity.Metadata.DeviceProduct);
        Assert.Null(activity.Metadata.DistanceMetres);
        Assert.Null(activity.Metadata.AscentMetres);

        Assert.NotNull(job);
        Assert.Equal(0, job!.ProgressPercent);
        Assert.Equal("queued", job.ProgressStage);
        Assert.Equal(createdAt, job.CreatedAt);
        Assert.Null(job.StartedAt);
        Assert.Equal(job.CreatedAt, job.UpdatedAt);
        Assert.Null(job.CompletedAt);

        Assert.Equal(
            new[] { "IX_analysis_jobs_queued_type_subject", "IX_analysis_jobs_running_type_subject" },
            await context.Database.SqlQueryRaw<string>(
                """
                SELECT indexname AS "Value"
                FROM pg_indexes
                WHERE schemaname = 'public'
                  AND tablename = 'analysis_jobs'
                  AND indexname IN ('IX_analysis_jobs_queued_type_subject', 'IX_analysis_jobs_running_type_subject')
                ORDER BY indexname
                """).ToListAsync());
        Assert.Null(await ScalarAsync<string?>(context, "SELECT to_regclass('\"IX_analysis_jobs_active_type_subject\"')::text"));
        Assert.Equal(1L, await ScalarAsync<long>(context, "SELECT COUNT(*) FROM pg_constraint WHERE conname = 'CK_analysis_jobs_progress'"));
    }

    private static async Task<T> ScalarAsync<T>(RouteTimerDbContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private sealed class DoublePrecisionComparer(int precision) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Round(x, precision) == Math.Round(y, precision);
        public int GetHashCode(double obj) => Math.Round(obj, precision).GetHashCode();
    }
}

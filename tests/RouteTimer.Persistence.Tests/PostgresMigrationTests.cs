using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouteTimer.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace RouteTimer.Persistence.Tests;

public sealed class PostgresMigrationTests
{
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

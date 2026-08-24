using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
        command.CommandText = "SELECT array_agg(to_regclass(table_name)::text ORDER BY table_name) FROM (VALUES ('predictions'), ('rider_profile'), ('stored_uploads'), ('training_activities'), ('activity_samples'), ('rider_models'), ('power_bands')) AS expected_tables(table_name)";
        await context.Database.OpenConnectionAsync();
        var table = await command.ExecuteScalarAsync();

        Assert.Equal(new[] { "activity_samples", "power_bands", "predictions", "rider_models", "rider_profile", "stored_uploads", "training_activities" }, (string[]?)table);
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
}

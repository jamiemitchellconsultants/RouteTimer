using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RouteTimer.Api.Health;
using RouteTimer.Persistence;

namespace RouteTimer.Api.Tests;

public sealed class DatabaseMigrationServiceTests
{
    [Fact]
    public async Task Marks_ready_immediately_for_a_non_relational_provider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<RouteTimerDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        await using var provider = services.BuildServiceProvider();
        var state = new MigrationState(migrationsRequired: true);
        var sut = new DatabaseMigrationService(provider, NullLogger<DatabaseMigrationService>.Instance, state);

        await sut.StartAsync(CancellationToken.None);

        Assert.True(state.IsReady);
    }

    [Fact]
    public async Task Leaves_readiness_unhealthy_when_the_connection_cannot_be_opened()
    {
        var services = new ServiceCollection();
        services.AddDbContext<RouteTimerDbContext>(options => options.UseNpgsql(
            "Host=127.0.0.1;Port=1;Database=nope;Username=x;Password=y;Timeout=1;Command Timeout=1"));
        await using var provider = services.BuildServiceProvider();
        var state = new MigrationState(migrationsRequired: true);
        var sut = new DatabaseMigrationService(provider, NullLogger<DatabaseMigrationService>.Instance, state);

        await Assert.ThrowsAnyAsync<Exception>(() => sut.StartAsync(CancellationToken.None));

        Assert.False(state.IsReady);
    }

    [Fact]
    public async Task Leaves_readiness_unhealthy_when_migration_fails_after_the_connection_opens()
    {
        // The regression this test exists to catch is specifically MarkCompleted() moving into the
        // finally block, which a connection-open failure alone cannot detect -- that mutation only
        // matters for a failure inside the try, after the connection succeeds. A connection to
        // SQLite opens fine (it is relational, so the non-relational early return does not apply),
        // but SQLite has no pg_advisory_lock function, so the failure happens exactly there: inside
        // the try, before MigrateAsync, with the finally block still running afterwards.
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<RouteTimerDbContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var state = new MigrationState(migrationsRequired: true);
        var sut = new DatabaseMigrationService(provider, NullLogger<DatabaseMigrationService>.Instance, state);

        await Assert.ThrowsAnyAsync<Exception>(() => sut.StartAsync(CancellationToken.None));

        Assert.False(state.IsReady);
    }

    // Not covered here: MarkCompleted() being deleted entirely from the successful-migration branch.
    // Proving the success path marks ready needs a real migration to actually succeed, which needs a
    // real PostgreSQL instance -- see PostgresMigrationTests in RouteTimer.Persistence.Tests for that
    // weight class. That gap would surface immediately in deployment verification: /health/ready
    // would never turn healthy and `docker compose up --wait` would time out rather than return
    // early, which is a loud failure rather than a silent one.
}

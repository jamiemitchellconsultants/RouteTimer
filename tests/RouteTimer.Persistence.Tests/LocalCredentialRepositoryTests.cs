using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace RouteTimer.Persistence.Tests;

public sealed class LocalCredentialRepositoryTests
{
    [Fact]
    public async Task Get_returns_null_before_a_credential_is_set()
    {
        await using var context = CreateContext();
        var repository = new LocalCredentialRepository(context);

        Assert.Null(await repository.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Set_then_get_round_trips_the_hash()
    {
        await using var context = CreateContext();
        var repository = new LocalCredentialRepository(context);

        await repository.SetAsync("hashed-value", CancellationToken.None);

        Assert.Equal("hashed-value", await repository.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Set_replaces_the_single_row_rather_than_adding_another()
    {
        await using var context = CreateContext();
        var repository = new LocalCredentialRepository(context);

        await repository.SetAsync("first", CancellationToken.None);
        await repository.SetAsync("second", CancellationToken.None);

        Assert.Equal("second", await repository.GetAsync(CancellationToken.None));
        Assert.Equal(1, await context.LocalCredentials.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TryAdd_stores_the_hash_and_reports_success_when_none_exists()
    {
        await using var context = CreateContext();
        var repository = new LocalCredentialRepository(context);

        var added = await repository.TryAddAsync("hashed-value", CancellationToken.None);

        Assert.True(added);
        Assert.Equal("hashed-value", await repository.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TryAdd_returns_false_and_leaves_the_winners_row_untouched_when_another_writer_already_inserted_it()
    {
        // Reproduces the exact interleaving that let a concurrent setup loser silently overwrite the
        // winner under the old read-then-upsert SetAsync: two separate DbContext instances (as two
        // concurrent requests would each get, since the repository is request-scoped) both read no
        // credential before either writes. TryAddAsync -- unlike SetAsync -- must let the database's
        // primary key be the sole arbiter of who wins, rather than either side's stale read.
        //
        // This runs against real Postgres rather than the EF Core InMemory provider deliberately:
        // InMemory raises a bare ArgumentException for a duplicate-key insert, not a
        // DbUpdateException, so it cannot exercise the same exception-wrapping path
        // LocalCredentialRepository.TryAddAsync and the caller both depend on in production.
        await using var database = await StartDatabaseAsync();
        await using (var migrationContext = CreateContext(database))
        {
            await migrationContext.Database.MigrateAsync();
        }

        await using var winnerContext = CreateContext(database);
        await using var loserContext = CreateContext(database);
        var winner = new LocalCredentialRepository(winnerContext);
        var loser = new LocalCredentialRepository(loserContext);

        Assert.Null(await winner.GetAsync(CancellationToken.None));
        Assert.Null(await loser.GetAsync(CancellationToken.None));

        Assert.True(await winner.TryAddAsync("winner-hash", CancellationToken.None));
        Assert.False(await loser.TryAddAsync("loser-hash", CancellationToken.None));

        await using var assertionContext = CreateContext(database);
        var assertionRepository = new LocalCredentialRepository(assertionContext);
        Assert.Equal("winner-hash", await assertionRepository.GetAsync(CancellationToken.None));
        Assert.Equal(1, await assertionContext.LocalCredentials.CountAsync(CancellationToken.None));
    }

    private static RouteTimerDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        return database;
    }

    private static RouteTimerDbContext CreateContext(PostgreSqlContainer database)
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .Options;
        return new RouteTimerDbContext(options);
    }
}

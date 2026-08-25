using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;

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

    private static RouteTimerDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}

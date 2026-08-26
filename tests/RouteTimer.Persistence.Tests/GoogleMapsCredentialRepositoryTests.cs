using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Security;

namespace RouteTimer.Persistence.Tests;

public sealed class GoogleMapsCredentialRepositoryTests
{
    [Fact]
    public async Task Returns_null_when_no_key_is_stored()
    {
        await using var context = CreateContext();
        var repository = new GoogleMapsCredentialRepository(context);

        Assert.Null(await repository.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Saves_and_replaces_the_single_row()
    {
        await using var context = CreateContext();
        var repository = new GoogleMapsCredentialRepository(context);
        var first = new GoogleMapsCredentialRecord(
            new ProtectedSecret(1, new byte[12], [1, 2, 3], new byte[16]),
            "aaaa…zzzz",
            DateTimeOffset.UnixEpoch);
        var second = first with
        {
            Secret = new ProtectedSecret(1, new byte[12], [4, 5, 6], new byte[16]),
            KeyHint = "bbbb…yyyy"
        };

        await repository.SaveAsync(first, CancellationToken.None);
        await repository.SaveAsync(second, CancellationToken.None);

        var stored = await repository.GetAsync(CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("bbbb…yyyy", stored.KeyHint);
        Assert.Equal(new byte[] { 4, 5, 6 }, stored.Secret.Ciphertext);
        Assert.Equal(1, await context.GoogleMapsCredentials.CountAsync());
    }

    [Fact]
    public async Task Deletes_the_stored_key_and_tolerates_a_missing_one()
    {
        await using var context = CreateContext();
        var repository = new GoogleMapsCredentialRepository(context);

        await repository.DeleteAsync(CancellationToken.None);
        await repository.SaveAsync(
            new GoogleMapsCredentialRecord(
                new ProtectedSecret(1, new byte[12], [1], new byte[16]),
                "aaaa…zzzz",
                DateTimeOffset.UnixEpoch),
            CancellationToken.None);
        await repository.DeleteAsync(CancellationToken.None);

        Assert.Null(await repository.GetAsync(CancellationToken.None));
    }

    private static RouteTimerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new RouteTimerDbContext(options);
    }
}

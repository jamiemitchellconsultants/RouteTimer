using System.Text;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Garmin;
using RouteTimer.Services.Persistence;
using Testcontainers.PostgreSql;

namespace RouteTimer.Persistence.Tests;

public sealed class GarminConnectionRepositoryTests
{
    // Break caught: EF InMemory can hide PostgreSQL mapping, bytea, singleton-row, and persistence defects.
    [Fact]
    public async Task Repository_round_trips_updates_and_deletes_the_encrypted_singleton_through_PostgreSQL()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .Options;
        await using var context = new RouteTimerDbContext(options);
        await context.Database.MigrateAsync();
        var repository = new GarminConnectionRepository(context);
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        using var protector = new AesGcmGarminTokenProtector(key);
        var firstTime = new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        var secondTime = firstTime.AddMinutes(5);

        await repository.SaveAsync(
            new GarminConnectionRecord(
                "connected",
                "42",
                "Jamie",
                protector.Protect("{\"di_token\":\"secret\"}"),
                firstTime,
                firstTime),
            CancellationToken.None);
        context.ChangeTracker.Clear();

        var saved = Assert.IsType<GarminConnectionRecord>(await repository.GetAsync(CancellationToken.None));
        Assert.Equal("{\"di_token\":\"secret\"}", protector.Unprotect(saved.Token));
        Assert.Equal(12, saved.Token.Nonce.Length);
        Assert.Equal(16, saved.Token.Tag.Length);

        await repository.SaveAsync(saved with { State = "reconnect-required", UpdatedAt = secondTime }, CancellationToken.None);
        context.ChangeTracker.Clear();

        Assert.Equal(1, await context.GarminConnections.CountAsync());
        var updated = Assert.IsType<GarminConnectionRecord>(await repository.GetAsync(CancellationToken.None));
        Assert.Equal("reconnect-required", updated.State);
        Assert.Equal(secondTime, updated.UpdatedAt);
        Assert.DoesNotContain(
            "secret",
            Encoding.UTF8.GetString(await context.GarminConnections.Select(connection => connection.Ciphertext).SingleAsync()),
            StringComparison.Ordinal);

        await repository.DeleteAsync(CancellationToken.None);

        Assert.Null(await repository.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Repository_round_trips_only_protected_token_fields_and_safe_metadata()
    {
        await using var context = CreateContext();
        var repository = new GarminConnectionRepository(context);
        var token = new ProtectedGarminToken(1, new byte[12], [1, 2, 3], new byte[16]);
        var lastValidatedAt = new DateTimeOffset(2026, 8, 25, 10, 30, 0, TimeSpan.Zero);
        var updatedAt = lastValidatedAt.AddMinutes(1);

        await repository.SaveAsync(
            new GarminConnectionRecord("connected", "42", "Jamie", token, lastValidatedAt, updatedAt),
            CancellationToken.None);

        context.ChangeTracker.Clear();
        var saved = await repository.GetAsync(CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal("connected", saved.State);
        Assert.Equal("42", saved.GarminUserId);
        Assert.Equal("Jamie", saved.DisplayName);
        Assert.Equal(1, saved.Token.Version);
        Assert.Equal(token.Nonce, saved.Token.Nonce);
        Assert.Equal(token.Ciphertext, saved.Token.Ciphertext);
        Assert.Equal(token.Tag, saved.Token.Tag);
        Assert.Equal(lastValidatedAt, saved.LastValidatedAt);
        Assert.Equal(updatedAt, saved.UpdatedAt);
        Assert.DoesNotContain("di_token", Encoding.UTF8.GetString(context.GarminConnections.Single().Ciphertext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_updates_the_single_connection_row_with_id_one()
    {
        await using var context = CreateContext();
        var repository = new GarminConnectionRepository(context);
        var firstTime = new DateTimeOffset(2026, 8, 25, 11, 0, 0, TimeSpan.Zero);
        var secondTime = firstTime.AddMinutes(5);

        await repository.SaveAsync(Connection("connected", [1], firstTime), CancellationToken.None);
        await repository.SaveAsync(Connection("reconnect-required", [2], secondTime), CancellationToken.None);

        var entity = Assert.Single(context.GarminConnections);
        Assert.Equal(1, entity.Id);
        Assert.Equal("reconnect-required", entity.State);
        Assert.Equal(new byte[] { 2 }, entity.Ciphertext);
        Assert.Equal(secondTime, entity.UpdatedAt);
    }

    [Fact]
    public async Task DeleteAsync_is_idempotent_and_removes_only_the_connection()
    {
        await using var context = CreateContext();
        var repository = new GarminConnectionRepository(context);
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await repository.SaveAsync(Connection("connected", [1], now), CancellationToken.None);

        await repository.DeleteAsync(CancellationToken.None);
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

    private static GarminConnectionRecord Connection(string state, byte[] ciphertext, DateTimeOffset instant) =>
        new(state, "42", "Jamie", new ProtectedGarminToken(1, new byte[12], ciphertext, new byte[16]), instant, instant);
}

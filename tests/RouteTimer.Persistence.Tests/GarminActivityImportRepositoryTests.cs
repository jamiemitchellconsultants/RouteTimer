using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RouteTimer.Persistence.Entities;
using RouteTimer.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace RouteTimer.Persistence.Tests;

public sealed class GarminActivityImportRepositoryTests
{
    // Break caught: imported-state projection could issue one query per row, compare IDs loosely, or emit invalid SQL for no rows.
    [Fact]
    public async Task GetLinkedIds_uses_one_PostgreSQL_query_with_ordinal_results_and_no_query_for_empty_input()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        var commands = new ReaderCommandCounter();
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .AddInterceptors(commands)
            .Options;
        await using var context = new RouteTimerDbContext(options);
        await context.Database.MigrateAsync();
        var now = new DateTimeOffset(2026, 8, 25, 14, 0, 0, TimeSpan.Zero);
        var roadUpload = Upload("road.fit", 1, now);
        var gravelUpload = Upload("gravel.fit", 2, now);
        context.Uploads.AddRange(roadUpload, gravelUpload);
        context.GarminActivityImports.AddRange(
            Import("ride", roadUpload.Id, "Road ride", now),
            Import("gravel", gravelUpload.Id, "Gravel ride", now));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var repository = new GarminActivityImportRepository(context);
        commands.Reset();

        var linked = await repository.GetLinkedIdsAsync(
            ["missing", "ride", "RIDE", "gravel"],
            CancellationToken.None);

        Assert.Equal(1, commands.ReaderCommands);
        Assert.Equal(["gravel", "ride"], linked.Order(StringComparer.Ordinal));
        Assert.True(linked.Contains("ride"));
        Assert.False(linked.Contains("RIDE"));

        commands.Reset();
        var empty = await repository.GetLinkedIdsAsync([], CancellationToken.None);

        Assert.Empty(empty);
        Assert.Equal(0, commands.ReaderCommands);
    }

    private static StoredUploadEntity Upload(string fileName, byte hashByte, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        Kind = "fit",
        FileName = fileName,
        Content = [hashByte],
        Sha256 = Enumerable.Repeat(hashByte, 32).ToArray(),
        CreatedAt = createdAt
    };

    private static GarminActivityImportEntity Import(
        string activityId,
        Guid uploadId,
        string activityName,
        DateTimeOffset linkedAt) => new()
    {
        GarminActivityId = activityId,
        UploadId = uploadId,
        ActivityName = activityName,
        LinkedAt = linkedAt
    };

    private sealed class ReaderCommandCounter : DbCommandInterceptor
    {
        public int ReaderCommands { get; private set; }

        public void Reset() => ReaderCommands = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands++;
            return ValueTask.FromResult(result);
        }
    }
}

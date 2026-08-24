using Microsoft.EntityFrameworkCore;
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
        command.CommandText = "SELECT to_regclass('public.stored_uploads')::text";
        await context.Database.OpenConnectionAsync();
        var table = await command.ExecuteScalarAsync();

        Assert.Equal("stored_uploads", table);
    }
}

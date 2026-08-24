using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence;

namespace RouteTimer.Api;

public sealed class DatabaseMigrationService(IServiceProvider services, ILogger<DatabaseMigrationService> logger) : IHostedService
{
    private const long MigrationLockId = 7_290_101;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        if (!database.Database.IsRelational())
        {
            return;
        }

        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await database.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({MigrationLockId})", cancellationToken);
            await database.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("RouteTimer database migrations completed.");
        }
        finally
        {
            await database.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({MigrationLockId})", cancellationToken);
            await database.Database.CloseConnectionAsync();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

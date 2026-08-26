using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RouteTimer.Api.Health;

public sealed class MigrationsReadyHealthCheck(MigrationState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsReady
            ? HealthCheckResult.Healthy("Database migrations are complete.")
            : HealthCheckResult.Unhealthy("Database migrations have not completed yet."));
}

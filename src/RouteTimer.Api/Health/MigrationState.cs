namespace RouteTimer.Api.Health;

/// <summary>
/// Tracks whether startup migrations have finished. The migration service is a hosted service and
/// starts after the web host's own, so Kestrel is already listening while migrations run. Without
/// this flag, readiness would report healthy against a database that is still migrating, and
/// Compose's --wait would return early.
/// </summary>
public sealed class MigrationState(bool migrationsRequired)
{
    private volatile bool completed;

    public bool IsReady => !migrationsRequired || completed;

    public void MarkCompleted() => completed = true;
}

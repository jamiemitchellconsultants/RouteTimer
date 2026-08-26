namespace RouteTimer.Api.Health;

/// <summary>
/// Tracks whether startup migrations have finished. Under this app's minimal hosting, user-registered
/// hosted services -- including the migration service -- run to completion before the framework's own
/// GenericWebHostService starts, so Kestrel does not currently bind a port until migrations are done.
///
/// This flag does not rely on that ordering holding. If migrations ever move onto a BackgroundService
/// (a common refactor, since a long StartAsync blocks the whole host), or HostOptions.ServicesStartConcurrently
/// is ever enabled, Kestrel could begin serving before migrations finish -- and without this flag,
/// readiness would then report healthy against a database still mid-migration, with Compose's --wait
/// returning early. This is insurance against that, not a fix for a gap that exists today.
/// </summary>
public sealed class MigrationState(bool migrationsRequired)
{
    private volatile bool completed;

    public bool IsReady => !migrationsRequired || completed;

    public void MarkCompleted() => completed = true;
}

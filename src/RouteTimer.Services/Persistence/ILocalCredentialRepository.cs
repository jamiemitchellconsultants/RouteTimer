namespace RouteTimer.Services.Persistence;

/// <summary>
/// Stores the single local-mode passphrase hash. At most one credential exists; this is a
/// single-rider deployment, not a user store.
/// </summary>
public interface ILocalCredentialRepository
{
    /// <summary>Returns the stored hash, or null when first-run setup has not happened yet.</summary>
    Task<string?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Stores the hash, replacing any existing one.</summary>
    Task SetAsync(string passwordHash, CancellationToken cancellationToken);
}

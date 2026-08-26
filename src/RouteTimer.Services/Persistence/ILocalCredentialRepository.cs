namespace RouteTimer.Services.Persistence;

/// <summary>
/// Stores the single local-mode passphrase hash. At most one credential exists; this is a
/// single-rider deployment, not a user store.
/// </summary>
public interface ILocalCredentialRepository
{
    /// <summary>Returns the stored hash, or null when first-run setup has not happened yet.</summary>
    Task<string?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Stores the hash, replacing any existing one. Used only to upgrade an existing hash in place.</summary>
    Task SetAsync(string passwordHash, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to create the first-ever credential. Insert-only: never overwrites an existing row.
    /// Two concurrent first-run callers can both observe <see cref="GetAsync"/> returning null before
    /// either writes, so the read alone cannot be trusted to decide "setup is allowed" -- this write
    /// is the authoritative check. Returns true if this call created the row, false if a row already
    /// existed (whether seen earlier or only discovered now, by losing the insert race).
    /// </summary>
    Task<bool> TryAddAsync(string passwordHash, CancellationToken cancellationToken);
}

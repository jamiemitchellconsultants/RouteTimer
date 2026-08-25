namespace RouteTimer.Api.Auth;

/// <summary>
/// Caches the local-mode "is setup required" check that the cookie scheme's OnValidatePrincipal
/// uses to revoke a session once the credential row is gone. Without this, every cookie-bearing
/// request -- API calls, static files (a Blazor WASM boot fetches 100+ of them), health checks --
/// costs a database read solely to confirm nothing changed, even though the credential row
/// essentially never changes outside a rider hand-deleting it to run first-use setup again.
/// </summary>
/// <remarks>
/// Registered as a singleton (see Program.cs) so the cache is shared across requests rather than
/// per-scope. Thread-safety is a plain lock around the two fields together, not a single-flight
/// dedupe: concurrent requests that all miss right at expiry may each issue their own database
/// read. That is an acceptable, rare cost -- what the lock actually guards against is a request
/// observing a torn combination of "old expiry, new result" or vice versa.
/// </remarks>
public sealed class CredentialRevalidationCache(TimeProvider timeProvider, TimeSpan ttl)
{
    /// <summary>
    /// How long a cached "setup is configured" result may be reused before the next request
    /// re-checks the database. This trades the responsiveness of the hand-deletion recovery path
    /// (an existing session can outlive the deleted row by up to this long, plus however long
    /// until the rider's next request) against read volume on every other request. 30 seconds is
    /// short enough that nobody performing that recovery notices a delay, and long enough to turn
    /// a roughly 100-request WASM boot into effectively one database read.
    /// </summary>
    public const int DefaultTtlSeconds = 30;

    private readonly object gate = new();
    private bool cachedIsSetupRequired;
    private DateTimeOffset expiresAt = DateTimeOffset.MinValue;

    public async Task<bool> IsSetupRequiredAsync(LocalCredentialService credentials, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (timeProvider.GetUtcNow() < expiresAt)
            {
                return cachedIsSetupRequired;
            }
        }

        var result = await credentials.IsSetupRequiredAsync(cancellationToken);

        lock (gate)
        {
            cachedIsSetupRequired = result;
            expiresAt = timeProvider.GetUtcNow() + ttl;
        }

        return result;
    }
}

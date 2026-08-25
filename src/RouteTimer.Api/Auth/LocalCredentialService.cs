using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Auth;

public enum LocalCredentialSetupResult
{
    Configured,
    AlreadyConfigured,
    TooShort,

    /// <summary>
    /// The passphrase has leading or trailing whitespace. Rejected rather than trimmed: silently
    /// trimming would validate one string while hashing another, and a passphrase padded to the
    /// minimum length (e.g. "a" followed by eleven spaces) would otherwise pass length validation
    /// while carrying almost no real entropy.
    /// </summary>
    Padded
}

/// <summary>
/// Owns the local-mode passphrase. Hashing uses the framework's <see cref="PasswordHasher{TUser}"/>,
/// which is available from the ASP.NET Core shared framework and carries its own versioned format,
/// so no hashing scheme is written here.
/// </summary>
public sealed class LocalCredentialService(ILocalCredentialRepository credentials, ILogger<LocalCredentialService> logger)
{
    /// <summary>
    /// Long enough to resist casual guessing, short enough that a rider will actually use a
    /// passphrase rather than a reused password. Enforced on setup only; existing credentials are
    /// never re-validated against a later change to this value.
    /// </summary>
    public const int MinimumPassphraseLength = 12;

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object HashSubject = new();

    public async Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken) =>
        await credentials.GetAsync(cancellationToken) is null;

    public async Task<LocalCredentialSetupResult> SetupAsync(string passphrase, CancellationToken cancellationToken)
    {
        if (await credentials.GetAsync(cancellationToken) is not null)
        {
            return LocalCredentialSetupResult.AlreadyConfigured;
        }

        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < MinimumPassphraseLength)
        {
            return LocalCredentialSetupResult.TooShort;
        }

        if (passphrase.Length != passphrase.Trim().Length)
        {
            return LocalCredentialSetupResult.Padded;
        }

        await credentials.SetAsync(Hasher.HashPassword(HashSubject, passphrase), cancellationToken);
        return LocalCredentialSetupResult.Configured;
    }

    public async Task<bool> VerifyAsync(string passphrase, CancellationToken cancellationToken)
    {
        var storedHash = await credentials.GetAsync(cancellationToken);
        // Returns early without hashing when no credential exists. This leaks first-run state by
        // timing, which is not a secret: /api/auth/config publishes setupRequired to anonymous
        // callers by design. The comparison that must be constant-time -- correct versus incorrect
        // passphrase -- is, inside the framework's verifier.
        if (storedHash is null || string.IsNullOrEmpty(passphrase))
        {
            return false;
        }

        PasswordVerificationResult outcome;
        try
        {
            outcome = Hasher.VerifyHashedPassword(HashSubject, storedHash, passphrase);
        }
        catch (FormatException)
        {
            // The stored value is not a hash this hasher wrote -- most likely a hand-edited row.
            // Treat it as no usable credential rather than a server error; recovery is deleting the row.
            return false;
        }

        if (outcome == PasswordVerificationResult.SuccessRehashNeeded)
        {
            try
            {
                await credentials.SetAsync(Hasher.HashPassword(HashSubject, passphrase), cancellationToken);
            }
            catch (Exception exception)
            {
                // The passphrase is correct. Failing to upgrade the stored hash must not deny the login.
                logger.LogWarning(exception, "Could not upgrade the stored passphrase hash; the existing hash remains valid.");
            }

            return true;
        }

        return outcome == PasswordVerificationResult.Success;
    }
}

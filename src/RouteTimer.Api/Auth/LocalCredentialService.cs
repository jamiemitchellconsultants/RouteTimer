using Microsoft.AspNetCore.Identity;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Auth;

public enum LocalCredentialSetupResult
{
    Configured,
    AlreadyConfigured,
    TooShort
}

/// <summary>
/// Owns the local-mode passphrase. Hashing uses the framework's <see cref="PasswordHasher{TUser}"/>,
/// which is available from the ASP.NET Core shared framework and carries its own versioned format,
/// so no hashing scheme is written here.
/// </summary>
public sealed class LocalCredentialService(ILocalCredentialRepository credentials)
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

        await credentials.SetAsync(Hasher.HashPassword(HashSubject, passphrase), cancellationToken);
        return LocalCredentialSetupResult.Configured;
    }

    public async Task<bool> VerifyAsync(string passphrase, CancellationToken cancellationToken)
    {
        var storedHash = await credentials.GetAsync(cancellationToken);
        if (storedHash is null || string.IsNullOrEmpty(passphrase))
        {
            return false;
        }

        var outcome = Hasher.VerifyHashedPassword(HashSubject, storedHash, passphrase);
        if (outcome == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await credentials.SetAsync(Hasher.HashPassword(HashSubject, passphrase), cancellationToken);
            return true;
        }

        return outcome == PasswordVerificationResult.Success;
    }
}

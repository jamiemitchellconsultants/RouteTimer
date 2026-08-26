using RouteTimer.Services.Persistence;
using RouteTimer.Services.Security;

namespace RouteTimer.Services.Settings;

public sealed class GoogleMapsKeyStorageUnavailableException()
    : Exception("Google Maps key storage is not configured on this deployment.");

public sealed class GoogleMapsKeyNotStoredException()
    : Exception("No Google Maps API key is stored.");

public sealed class GoogleMapsKeyInvalidException()
    : Exception("The Google Maps API key is empty or too long.");

public sealed record GoogleMapsKeyStatus(bool Configured, string? Hint, bool StorageAvailable);

public sealed class GoogleMapsKeyService(
    IGoogleMapsCredentialRepository repository,
    ISecretProtector? protector,
    TimeProvider timeProvider)
{
    public const string Purpose = "RouteTimer:GoogleMapsKey:1:1";
    private const int MaximumKeyLength = 512;
    private const int MinimumMaskableLength = 8;

    public async Task<GoogleMapsKeyStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (protector is null)
        {
            return new GoogleMapsKeyStatus(false, null, false);
        }

        var stored = await repository.GetAsync(cancellationToken);
        return new GoogleMapsKeyStatus(stored is not null, stored?.KeyHint, true);
    }

    public async Task SaveAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (protector is null)
        {
            throw new GoogleMapsKeyStorageUnavailableException();
        }

        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > MaximumKeyLength)
        {
            throw new GoogleMapsKeyInvalidException();
        }

        // Beyond "not empty and not absurdly long", nothing about the key is validated. Google is
        // the only authority on whether a key works, and asserting a shape here would reject keys
        // that Google itself accepts.
        var trimmed = apiKey.Trim();
        await repository.SaveAsync(
            new GoogleMapsCredentialRecord(protector.Protect(trimmed), Mask(trimmed), timeProvider.GetUtcNow()),
            cancellationToken);
    }

    public async Task<string> RevealAsync(CancellationToken cancellationToken)
    {
        if (protector is null)
        {
            throw new GoogleMapsKeyStorageUnavailableException();
        }

        var stored = await repository.GetAsync(cancellationToken)
            ?? throw new GoogleMapsKeyNotStoredException();
        return protector.Unprotect(stored.Secret);
    }

    public Task DeleteAsync(CancellationToken cancellationToken) => repository.DeleteAsync(cancellationToken);

    // Matches the client-side KeyRedactor.Mask and the mask() in gmaps.js, so the hint the API
    // stores and the redaction the log applies are the same string.
    private static string Mask(string key) =>
        key.Length < MinimumMaskableLength ? "…" : $"{key[..4]}…{key[^4..]}";
}

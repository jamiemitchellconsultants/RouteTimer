using RouteTimer.Services.Security;

namespace RouteTimer.Services.Persistence;

public sealed record GoogleMapsCredentialRecord(ProtectedSecret Secret, string KeyHint, DateTimeOffset UpdatedAt);

public interface IGoogleMapsCredentialRepository
{
    Task<GoogleMapsCredentialRecord?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(GoogleMapsCredentialRecord credential, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
}

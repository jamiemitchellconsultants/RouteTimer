using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Tests.Settings;

public sealed class FakeGoogleMapsCredentialRepository : IGoogleMapsCredentialRepository
{
    private GoogleMapsCredentialRecord? stored;

    public Task<GoogleMapsCredentialRecord?> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(stored);

    public Task SaveAsync(GoogleMapsCredentialRecord credential, CancellationToken cancellationToken)
    {
        stored = credential;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        stored = null;
        return Task.CompletedTask;
    }
}

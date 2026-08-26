using RouteTimer.Services.Garmin;

namespace RouteTimer.Services.Persistence;

public sealed record GarminConnectionRecord(
    string State,
    string? GarminUserId,
    string? DisplayName,
    ProtectedGarminToken Token,
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset UpdatedAt);

public interface IGarminConnectionRepository
{
    Task<GarminConnectionRecord?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(GarminConnectionRecord connection, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
}

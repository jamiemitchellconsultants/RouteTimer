using RouteTimer.Domain.Profile;

namespace RouteTimer.Services.Persistence;

public interface IProfileRepository
{
    Task<RiderProfile?> GetAsync(CancellationToken cancellationToken);
    Task SaveAsync(RiderProfile profile, CancellationToken cancellationToken);
}

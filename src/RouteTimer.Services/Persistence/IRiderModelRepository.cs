using RouteTimer.Domain.Models;
using RouteTimer.Domain.Profile;

namespace RouteTimer.Services.Persistence;

public interface IRiderModelRepository
{
    Task<Guid> SaveAsync(RiderModel model, RiderProfile profileSnapshot, ModelValidationSummary validation, CancellationToken cancellationToken);

    Task<RiderModelSnapshot?> GetCurrentAsync(CancellationToken cancellationToken);

    Task<RiderModelSnapshot?> GetAsync(Guid modelId, CancellationToken cancellationToken);
}

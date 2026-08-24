using RouteTimer.Domain.Activities;

namespace RouteTimer.Services.Persistence;

public interface ITrainingActivityRepository
{
    Task<Guid> SaveAsync(Guid uploadId, CleanedActivity activity, CancellationToken cancellationToken);

    Task<CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken);
}

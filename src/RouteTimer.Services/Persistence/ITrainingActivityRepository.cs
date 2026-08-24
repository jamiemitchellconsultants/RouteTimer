using RouteTimer.Domain.Activities;

namespace RouteTimer.Services.Persistence;

public interface ITrainingActivityRepository
{
    Task<Guid> SaveAsync(Guid uploadId, CleanedActivity activity, CancellationToken cancellationToken);

    Task<CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken);

    /// <summary>Returns every persisted training activity, eligible and ineligible alike. Callers that need
    /// only eligible evidence (e.g. the model builder) are responsible for filtering.</summary>
    Task<IReadOnlyList<CleanedActivity>> GetAllAsync(CancellationToken cancellationToken);
}

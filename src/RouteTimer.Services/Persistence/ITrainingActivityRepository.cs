using RouteTimer.Domain.Activities;

namespace RouteTimer.Services.Persistence;

public sealed record TrainingActivitySummary(
    Guid Id,
    Guid UploadId,
    TrainingActivityMetadata Metadata,
    TimeSpan MovingDuration,
    ActivityEligibility Eligibility,
    double PositionCoverage,
    double ElevationCoverage,
    double SpeedCoverage,
    double PowerCoverage,
    IReadOnlyList<string> ReasonCodes,
    DateTimeOffset CreatedAt);

public sealed record TrainingActivityDetail(
    TrainingActivitySummary Summary,
    IReadOnlyDictionary<string, int> ExclusionCounts);

public sealed record TrainingActivityCounts(int Total, int Eligible);

public interface ITrainingActivityRepository
{
    Task<Guid> SaveAsync(Guid uploadId, CleanedActivity activity, CancellationToken cancellationToken);

    Task<CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken);

    /// <summary>Returns every persisted training activity, eligible and ineligible alike. Callers that need
    /// only eligible evidence (e.g. the model builder) are responsible for filtering.</summary>
    Task<IReadOnlyList<CleanedActivity>> GetAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TrainingActivitySummary>> GetSummariesAsync(CancellationToken cancellationToken);

    Task<TrainingActivityDetail?> GetDetailAsync(Guid activityId, CancellationToken cancellationToken);

    Task<TrainingActivityCounts> GetCountsAsync(CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid activityId, CancellationToken cancellationToken);
}

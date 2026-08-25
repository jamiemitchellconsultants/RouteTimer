using RouteTimer.Domain.Jobs;

namespace RouteTimer.Services.Jobs;

public interface IJobRepository
{
    Task<AnalysisJob?> GetAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the latest matching job ordered by CreatedAt descending, UpdatedAt descending, then Id descending.
    /// </summary>
    /// <remarks>
    /// UpdatedAt resolves jobs created at the same instant; Id resolves exact timestamp ties deterministically.
    /// </remarks>
    Task<AnalysisJob?> GetLatestAsync(JobType type, Guid subjectId, CancellationToken cancellationToken);
}

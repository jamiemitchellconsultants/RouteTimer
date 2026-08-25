using RouteTimer.Domain.Jobs;

namespace RouteTimer.Services.Jobs;

public interface IJobRepository
{
    Task<AnalysisJob?> GetAsync(Guid jobId, CancellationToken cancellationToken);

    Task<AnalysisJob?> GetLatestAsync(JobType type, Guid subjectId, CancellationToken cancellationToken);
}

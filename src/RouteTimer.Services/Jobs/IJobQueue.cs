using RouteTimer.Domain.Jobs;

namespace RouteTimer.Services.Jobs;

public interface IJobQueue
{
    Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken);
    Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
}

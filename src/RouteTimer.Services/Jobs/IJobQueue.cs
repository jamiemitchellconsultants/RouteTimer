using RouteTimer.Domain.Jobs;

namespace RouteTimer.Services.Jobs;

public interface IJobQueue
{
    Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken);
    Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task CompleteAsync(Guid jobId, CancellationToken cancellationToken);
    Task FailAsync(Guid jobId, bool permanent, string? diagnosticCode, string? diagnosticMessage, CancellationToken cancellationToken);
}

using RouteTimer.Domain.Jobs;

namespace RouteTimer.Services.Jobs;

public interface IJobQueue
{
    Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken);
    Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken);
    Task<bool> FailAsync(Guid jobId, string workerId, bool permanent, string? diagnosticCode, string? diagnosticMessage, CancellationToken cancellationToken);
}

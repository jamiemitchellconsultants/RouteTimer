using RouteTimer.Domain.Jobs;

namespace RouteTimer.Services.Jobs;

public interface IJobQueue
{
    Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken);

    /// <summary>
    /// Enqueues a new job unless one of the same <paramref name="type"/> and <paramref name="subjectId"/>
    /// is already <c>Queued</c> or <c>Running</c>, in which case that job's id is returned instead and no
    /// new row is inserted. This is a simple coalescing/dedupe mechanism: at most one active job for a
    /// given (type, subject) pair exists at a time. Safe under concurrent callers.
    /// </summary>
    Task<Guid> EnqueueIfNotPendingAsync(JobType type, Guid subjectId, CancellationToken cancellationToken);
    Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> FailAsync(Guid jobId, string workerId, bool permanent, string? diagnosticCode, string? diagnosticMessage, DateTimeOffset now, CancellationToken cancellationToken);
}

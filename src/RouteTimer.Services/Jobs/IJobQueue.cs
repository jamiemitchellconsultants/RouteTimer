using RouteTimer.Domain.Jobs;

namespace RouteTimer.Services.Jobs;

public interface IJobQueue
{
    Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken);

    /// <summary>
    /// Enqueues a new queued job unless one of the same <paramref name="type"/> and <paramref name="subjectId"/>
    /// is already <c>Queued</c>, in which case that queued job's id is returned instead and no new row
    /// is inserted. A concurrently running row for the same pair does not block insertion of a single
    /// queued successor. Safe under concurrent callers.
    /// </summary>
    Task<Guid> EnqueueIfNotPendingAsync(JobType type, Guid subjectId, CancellationToken cancellationToken);
    Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> ReportProgressAsync(Guid jobId, string workerId, int progressPercent, string stage, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> FailAsync(Guid jobId, string workerId, bool permanent, string? diagnosticCode, string? diagnosticMessage, DateTimeOffset now, CancellationToken cancellationToken);
}

using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;

namespace RouteTimer.Persistence.Jobs;

public sealed class PostgresJobQueue : IJobQueue
{
    private readonly object gate = new();
    private readonly List<AnalysisJob> jobs = [];

    public Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        lock (gate)
        {
            jobs.Add(new AnalysisJob(id, type, subjectId, JobState.Queued, 0, null, null, DateTimeOffset.UtcNow));
        }
        return Task.FromResult(id);
    }

    public Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var index = jobs.FindIndex(job => job.State == JobState.Queued || (job.State == JobState.Running && job.LeaseExpiresAt <= now));
            if (index < 0) return Task.FromResult<AnalysisJob?>(null);
            var job = jobs[index] with { State = JobState.Running, WorkerId = workerId, LeaseExpiresAt = now.Add(leaseDuration), AttemptCount = jobs[index].AttemptCount + 1 };
            jobs[index] = job;
            return Task.FromResult<AnalysisJob?>(job);
        }
    }
}

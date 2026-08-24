using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;

namespace RouteTimer.Persistence.Jobs;

public sealed class PostgresJobQueue(RouteTimerDbContext context) : IJobQueue
{
    public async Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        context.Jobs.Add(new AnalysisJobEntity { Id = id, Type = type.ToString(), SubjectId = subjectId, State = JobState.Queued.ToString(), CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(cancellationToken);
        return id;
    }

    public async Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        cancellationToken.ThrowIfCancellationRequested();
        var job = await context.Jobs.OrderBy(entity => entity.CreatedAt).FirstOrDefaultAsync(entity => entity.State == JobState.Queued.ToString() || (entity.State == JobState.Running.ToString() && entity.LeaseExpiresAt <= now), cancellationToken);
        if (job is null) return null;
        job.State = JobState.Running.ToString(); job.WorkerId = workerId; job.LeaseExpiresAt = now.Add(leaseDuration); job.AttemptCount++;
        await context.SaveChangesAsync(cancellationToken);
        return new AnalysisJob(job.Id, Enum.Parse<JobType>(job.Type), job.SubjectId, JobState.Running, job.AttemptCount, job.WorkerId, job.LeaseExpiresAt, job.CreatedAt);
    }
}

using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;

namespace RouteTimer.Persistence.Repositories;

public sealed class JobRepository(RouteTimerDbContext context) : IJobRepository
{
    public async Task<AnalysisJob?> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await context.Jobs.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == jobId, cancellationToken);
        return job is null ? null : ToDomain(job);
    }

    // The ordering is part of the latest-job contract: newest CreatedAt, then newest UpdatedAt,
    // then the greatest Id to make exact timestamp ties deterministic.
    public async Task<AnalysisJob?> GetLatestAsync(JobType type, Guid subjectId, CancellationToken cancellationToken)
    {
        var job = await context.Jobs.AsNoTracking()
            .Where(entity => entity.Type == type.ToString() && entity.SubjectId == subjectId)
            .OrderByDescending(entity => entity.CreatedAt)
            .ThenByDescending(entity => entity.UpdatedAt)
            .ThenByDescending(entity => entity.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return job is null ? null : ToDomain(job);
    }

    private static AnalysisJob ToDomain(RouteTimer.Persistence.Entities.AnalysisJobEntity job) =>
        new(
            job.Id,
            Enum.Parse<JobType>(job.Type),
            job.SubjectId,
            Enum.Parse<JobState>(job.State),
            job.ProgressPercent,
            job.ProgressStage,
            job.AttemptCount,
            job.CreatedAt,
            job.StartedAt,
            job.UpdatedAt,
            job.CompletedAt,
            job.WorkerId,
            job.LeaseExpiresAt,
            job.DiagnosticCode,
            job.DiagnosticMessage);
}

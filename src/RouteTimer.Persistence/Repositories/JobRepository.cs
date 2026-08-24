using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;

namespace RouteTimer.Persistence.Repositories;

public sealed class JobRepository(RouteTimerDbContext context) : IJobRepository
{
    public async Task<AnalysisJob?> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await context.Jobs.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == jobId, cancellationToken);
        return job is null ? null : new AnalysisJob(job.Id, Enum.Parse<JobType>(job.Type), job.SubjectId, Enum.Parse<JobState>(job.State), job.AttemptCount,
            job.WorkerId, job.LeaseExpiresAt, job.CreatedAt, job.DiagnosticCode, job.DiagnosticMessage);
    }
}

using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;

namespace RouteTimer.Persistence.Jobs;

public sealed class PostgresJobQueue(RouteTimerDbContext context) : IJobQueue
{
    /// <summary>Bounded retries: a job may be attempted at most this many times before it becomes permanently Failed.</summary>
    public const int MaxAttempts = 3;

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

        var queued = JobState.Queued.ToString();
        var running = JobState.Running.ToString();

        // A single transaction spans the lock-acquiring SELECT and the subsequent UPDATE so that no
        // other connection can see or claim this row in between: FOR UPDATE SKIP LOCKED lets concurrent
        // callers each grab a different eligible row (or none) instead of blocking on or duplicating one.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var candidateIds = await context.Database.SqlQuery<Guid>(
            $"""
            SELECT "Id" AS "Value" FROM analysis_jobs
            WHERE "State" = {queued} OR ("State" = {running} AND "LeaseExpiresAt" <= {now})
            ORDER BY "CreatedAt"
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (candidateIds.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var candidateId = candidateIds[0];
        var job = await context.Jobs.SingleAsync(entity => entity.Id == candidateId, cancellationToken);
        job.State = JobState.Running.ToString();
        job.WorkerId = workerId;
        job.LeaseExpiresAt = now.Add(leaseDuration);
        job.AttemptCount++;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ToDomain(job);
    }

    public async Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        cancellationToken.ThrowIfCancellationRequested();

        var running = JobState.Running.ToString();
        var rows = await context.Jobs
            .Where(entity => entity.Id == jobId && entity.State == running && entity.WorkerId == workerId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.LeaseExpiresAt, now.Add(leaseDuration)), cancellationToken);
        return rows > 0;
    }

    public async Task CompleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Jobs
            .Where(entity => entity.Id == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.State, JobState.Succeeded.ToString()), cancellationToken);
    }

    public async Task FailAsync(Guid jobId, bool permanent, string? diagnosticCode, string? diagnosticMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var job = await context.Jobs.SingleAsync(entity => entity.Id == jobId, cancellationToken);
        job.DiagnosticCode = diagnosticCode;
        job.DiagnosticMessage = diagnosticMessage;

        if (permanent || job.AttemptCount >= MaxAttempts)
        {
            job.State = JobState.Failed.ToString();
        }
        else
        {
            job.State = JobState.Queued.ToString();
            job.WorkerId = null;
            job.LeaseExpiresAt = null;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static AnalysisJob ToDomain(AnalysisJobEntity job) =>
        new(job.Id, Enum.Parse<JobType>(job.Type), job.SubjectId, Enum.Parse<JobState>(job.State), job.AttemptCount, job.WorkerId, job.LeaseExpiresAt, job.CreatedAt, job.DiagnosticCode, job.DiagnosticMessage);
}

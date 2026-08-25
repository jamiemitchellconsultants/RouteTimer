using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;
using Npgsql;

namespace RouteTimer.Persistence.Jobs;

public sealed class PostgresJobQueue(RouteTimerDbContext context) : IJobQueue
{
    /// <summary>Bounded retries: a job may be attempted at most this many times before it becomes permanently Failed.</summary>
    public const int MaxAttempts = JobRetryPolicy.MaxAttempts;

    public async Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        context.Jobs.Add(CreateQueuedJob(id, type, subjectId, createdAt));
        await context.SaveChangesAsync(cancellationToken);
        return id;
    }

    public async Task<Guid> EnqueueIfNotPendingAsync(JobType type, Guid subjectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var attempt = await TryInsertQueuedJobAsync(type, subjectId, cancellationToken);
        if (attempt.HasValue)
        {
            return attempt.Value;
        }

        // The queued row that won the original race left the queued set (e.g. it was claimed or
        // completed) in the narrow window between our failed insert and the fallback lookup above, so
        // that lookup found nothing. The conflict that blocked us no longer exists, so retry the insert
        // once more - it should now succeed cleanly. A second conflict here is left to propagate
        // unhandled; this isn't a scenario that warrants unbounded retries.
        var id = Guid.NewGuid();
        context.Jobs.Add(CreateQueuedJob(id, type, subjectId, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync(cancellationToken);
        return id;
    }

    /// <summary>
    /// Attempts a single insert of a new Queued job. On success, returns its id. On a unique-index
    /// conflict (another caller already has a queued job for this (type, subjectId)), looks up that
    /// queued row and returns its id - unless it has already left the queued state by the time the
    /// lookup runs, in which case this returns null so the caller can retry the insert.
    /// </summary>
    private async Task<Guid?> TryInsertQueuedJobAsync(JobType type, Guid subjectId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var entity = CreateQueuedJob(id, type, subjectId, DateTimeOffset.UtcNow);
        context.Jobs.Add(entity);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return id;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent caller won the race to insert the queued (Type, SubjectId) row that the
            // partial unique index guards. Detach our failed entity so it doesn't linger in the change
            // tracker, then look up whichever queued job actually won and return its id instead.
            context.Entry(entity).State = EntityState.Detached;

            var queued = JobState.Queued.ToString();
            var existing = await context.Jobs
                .Where(job => job.Type == entity.Type && job.SubjectId == subjectId && job.State == queued)
                .OrderBy(job => job.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            return existing?.Id;
        }
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
        job.ProgressStage = "running";
        job.WorkerId = workerId;
        job.LeaseExpiresAt = now.Add(leaseDuration);
        job.AttemptCount++;
        job.StartedAt ??= now;
        job.UpdatedAt = now;
        job.CompletedAt = null;
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
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(entity => entity.LeaseExpiresAt, now.Add(leaseDuration))
                    .SetProperty(entity => entity.UpdatedAt, now),
                cancellationToken);
        return rows > 0;
    }

    public async Task<bool> CompleteAsync(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        cancellationToken.ThrowIfCancellationRequested();

        var running = JobState.Running.ToString();
        var rows = await context.Jobs
            .Where(entity => entity.Id == jobId && entity.State == running && entity.WorkerId == workerId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(entity => entity.State, JobState.Succeeded.ToString())
                    .SetProperty(entity => entity.ProgressPercent, 100)
                    .SetProperty(entity => entity.ProgressStage, "completed")
                    .SetProperty(entity => entity.UpdatedAt, now)
                    .SetProperty(entity => entity.CompletedAt, now)
                    .SetProperty(entity => entity.WorkerId, (string?)null)
                    .SetProperty(entity => entity.LeaseExpiresAt, (DateTimeOffset?)null),
                cancellationToken);
        return rows > 0;
    }

    public async Task<bool> FailAsync(Guid jobId, string workerId, bool permanent, string? diagnosticCode, string? diagnosticMessage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        cancellationToken.ThrowIfCancellationRequested();

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // The guarded read-then-save below happens on a single row keyed by its primary key, and every
        // mutation of an AnalysisJob row goes through ClaimAsync/RenewLeaseAsync/CompleteAsync/FailAsync -
        // none of which can interleave invisibly between this read and SaveChangesAsync - so this ownership
        // check is race-free without an explicit transaction or FOR UPDATE. The transaction is still
        // required here because a terminal PredictRoute failure must commit the job and prediction state
        // together.
        var running = JobState.Running.ToString();
        var job = await context.Jobs.SingleOrDefaultAsync(
            entity => entity.Id == jobId && entity.State == running && entity.WorkerId == workerId,
            cancellationToken);
        if (job is null)
        {
            return false;
        }

        job.DiagnosticCode = diagnosticCode;
        job.DiagnosticMessage = diagnosticMessage;
        job.UpdatedAt = now;

        if (permanent || job.AttemptCount >= MaxAttempts)
        {
            job.State = JobState.Failed.ToString();
            job.ProgressStage = "failed";
            job.WorkerId = null;
            job.LeaseExpiresAt = null;
            job.CompletedAt = now;

            if (job.Type == JobType.PredictRoute.ToString())
            {
                var prediction = await context.Predictions.SingleOrDefaultAsync(entity => entity.Id == job.SubjectId, cancellationToken);
                if (prediction is not null)
                {
                    prediction.State = PredictionState.Failed.ToString();
                    prediction.Warnings = [$"{diagnosticCode ?? "processing-error"}: {diagnosticMessage ?? "The prediction job failed."}"];
                    prediction.CompletedAt = now;
                }
            }
        }
        else
        {
            job.State = JobState.Queued.ToString();
            job.ProgressPercent = 0;
            job.ProgressStage = "queued";
            job.WorkerId = null;
            job.LeaseExpiresAt = null;
            job.CompletedAt = null;
        }

        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static AnalysisJobEntity CreateQueuedJob(Guid id, JobType type, Guid subjectId, DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
            Type = type.ToString(),
            SubjectId = subjectId,
            State = JobState.Queued.ToString(),
            ProgressPercent = 0,
            ProgressStage = "queued",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static AnalysisJob ToDomain(AnalysisJobEntity job) =>
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

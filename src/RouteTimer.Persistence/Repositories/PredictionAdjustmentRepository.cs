using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Models;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

/// <summary>
/// Owns the append-only <c>prediction_adjustments</c>/<c>prediction_adjustment_segments</c> aggregate.
/// Job rows are created and enqueued separately by the service layer via <c>IJobQueue</c>, mirroring
/// the <c>AdjustPrediction</c> job type string that <see cref="RouteTimer.Domain.Jobs.JobType"/> gains
/// once orchestration is wired up.
/// </summary>
public sealed class PredictionAdjustmentRepository(RouteTimerDbContext context) : IPredictionAdjustmentRepository
{
    private const string AdjustPredictionJobType = "AdjustPrediction";
    private const string Running = "Running";

    public async Task<QueuedAdjustmentCreationResult> CreateQueuedAsync(QueuedAdjustmentCreation creation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        if (context.Database.IsRelational())
        {
            await context.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM predictions WHERE "Id" = {creation.PredictionId} FOR UPDATE
                """).ToListAsync(cancellationToken);
        }

        var baseline = await context.Predictions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == creation.PredictionId, cancellationToken);
        if (baseline is null)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return new QueuedAdjustmentCreationResult(AdjustmentBaselineStatus.BaselineNotFound, null);
        }

        if (baseline.State != "Succeeded")
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return new QueuedAdjustmentCreationResult(AdjustmentBaselineStatus.BaselineNotReady, null);
        }

        var adjustment = new PredictionAdjustmentEntity
        {
            Id = Guid.NewGuid(),
            PredictionId = creation.PredictionId,
            StrategyType = creation.StrategyType.ToString(),
            StrategyJson = creation.StrategyJson,
            StrategyAlgorithmVersion = creation.StrategyAlgorithmVersion,
            State = AdjustmentState.Queued.ToString(),
            CreatedAt = creation.CreatedAt,
        };
        context.PredictionAdjustments.Add(adjustment);
        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new QueuedAdjustmentCreationResult(AdjustmentBaselineStatus.Ready, adjustment.Id);
    }

    public async Task<AdjustmentForProcessing?> GetForProcessingAsync(Guid adjustmentId, CancellationToken cancellationToken)
    {
        var adjustment = await context.PredictionAdjustments.AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == adjustmentId, cancellationToken);
        return adjustment is null
            ? null
            : new AdjustmentForProcessing(adjustment.Id, adjustment.PredictionId, Enum.Parse<PacingStrategyType>(adjustment.StrategyType), adjustment.StrategyJson);
    }

    public async Task<bool> TryPublishAsync(Guid adjustmentId, Guid jobId, string workerId, AdjustmentPublication publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        if (context.Database.IsRelational())
        {
            var matchingJob = await context.Database.SqlQuery<Guid>(
                $"""
                SELECT "Id" AS "Value" FROM analysis_jobs
                WHERE "Id" = {jobId}
                  AND "SubjectId" = {adjustmentId}
                  AND "Type" = {AdjustPredictionJobType}
                  AND "State" = {Running}
                  AND "WorkerId" = {workerId}
                FOR UPDATE
                """).ToListAsync(cancellationToken);
            if (matchingJob.Count == 0)
            {
                return false;
            }
        }
        else if (!await context.Jobs.AnyAsync(entity =>
                     entity.Id == jobId && entity.SubjectId == adjustmentId && entity.Type == AdjustPredictionJobType &&
                     entity.State == Running && entity.WorkerId == workerId, cancellationToken))
        {
            return false;
        }

        if (context.Database.IsRelational())
        {
            var matchingAdjustment = await context.Database.SqlQuery<Guid>(
                $"""
                SELECT "Id" AS "Value" FROM prediction_adjustments WHERE "Id" = {adjustmentId} FOR UPDATE
                """).ToListAsync(cancellationToken);
            if (matchingAdjustment.Count == 0)
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        var adjustment = await context.PredictionAdjustments.Include(entity => entity.Segments)
            .SingleOrDefaultAsync(entity => entity.Id == adjustmentId, cancellationToken);
        if (adjustment is null)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var baselineSequences = await context.PredictionSegments.AsNoTracking()
            .Where(entity => entity.PredictionId == adjustment.PredictionId)
            .Select(entity => entity.Sequence)
            .ToListAsync(cancellationToken);
        var publishedSequences = publication.Segments.Select(segment => segment.Sequence).ToList();
        if (baselineSequences.Count == 0 ||
            new HashSet<int>(baselineSequences).SetEquals(publishedSequences) is false ||
            publishedSequences.Distinct().Count() != publishedSequences.Count)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        adjustment.Segments.Clear();
        foreach (var segment in publication.Segments)
        {
            adjustment.Segments.Add(new PredictionAdjustmentSegmentEntity
            {
                AdjustmentId = adjustment.Id,
                Sequence = segment.Sequence,
                PowerWatts = segment.PowerWatts,
                SpeedMetresPerSecond = segment.SpeedMetresPerSecond,
                SegmentMovingSeconds = segment.SegmentMovingTime.TotalSeconds,
                CumulativeMovingSeconds = segment.CumulativeMovingTime.TotalSeconds,
                Confidence = segment.Confidence.ToString(),
                ZoneNumber = segment.ZoneNumber,
                StrategyPhase = segment.StrategyPhase,
                WPrimeBalanceJoules = segment.WPrimeBalanceJoules,
            });
        }

        adjustment.MovingSeconds = publication.MovingTime.TotalSeconds;
        adjustment.AverageSpeedMetresPerSecond = publication.AverageSpeedMetresPerSecond;
        adjustment.AveragePowerWatts = publication.AveragePowerWatts;
        adjustment.Confidence = publication.Confidence.ToString();
        adjustment.Warnings = publication.Warnings.ToList();
        adjustment.ResultJson = publication.ReportJson;
        adjustment.State = AdjustmentState.Succeeded.ToString();
        adjustment.CompletedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid predictionId, Guid adjustmentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var jobIds = context.Database.IsRelational()
            ? await context.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM analysis_jobs
                WHERE "SubjectId" = {adjustmentId} AND "Type" = {AdjustPredictionJobType}
                  AND "State" IN ('Queued', 'Running')
                FOR UPDATE
                """).ToListAsync(cancellationToken)
            : await context.Jobs
                .Where(entity => entity.SubjectId == adjustmentId && entity.Type == AdjustPredictionJobType &&
                    (entity.State == "Queued" || entity.State == "Running"))
                .Select(entity => entity.Id)
                .ToListAsync(cancellationToken);

        var adjustment = await context.PredictionAdjustments
            .SingleOrDefaultAsync(entity => entity.Id == adjustmentId && entity.PredictionId == predictionId, cancellationToken);
        if (adjustment is null)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var jobs = await context.Jobs.Where(entity => jobIds.Contains(entity.Id)).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.State = "Cancelled";
            job.ProgressStage = "cancelled";
            job.UpdatedAt = now;
            job.CompletedAt = now;
            job.WorkerId = null;
            job.LeaseExpiresAt = null;
            job.DiagnosticCode = null;
            job.DiagnosticMessage = null;
        }

        context.PredictionAdjustments.Remove(adjustment);
        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task FailAsync(Guid adjustmentId, string code, string message, CancellationToken cancellationToken)
    {
        var adjustment = await context.PredictionAdjustments.SingleOrDefaultAsync(entity => entity.Id == adjustmentId, cancellationToken);
        if (adjustment is null) return;
        adjustment.State = AdjustmentState.Failed.ToString();
        adjustment.Warnings = [$"{code}: {message}"];
        adjustment.CompletedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PredictionAdjustmentSummary>> GetSummariesAsync(Guid predictionId, CancellationToken cancellationToken) =>
        (await context.PredictionAdjustments.AsNoTracking()
            .Where(entity => entity.PredictionId == predictionId)
            .OrderByDescending(entity => entity.CreatedAt)
            .ToListAsync(cancellationToken))
        .Select(ToSummary)
        .ToList();

    public async Task<PredictionAdjustmentDetail?> GetAsync(Guid predictionId, Guid adjustmentId, CancellationToken cancellationToken)
    {
        var entity = await context.PredictionAdjustments.AsNoTracking()
            .Include(adjustment => adjustment.Segments)
            .SingleOrDefaultAsync(adjustment => adjustment.Id == adjustmentId && adjustment.PredictionId == predictionId, cancellationToken);
        return entity is null ? null : ToDetail(entity);
    }

    private static PredictionAdjustmentSummary ToSummary(PredictionAdjustmentEntity entity) => new(
        entity.Id, entity.PredictionId, Enum.Parse<PacingStrategyType>(entity.StrategyType), Enum.Parse<AdjustmentState>(entity.State),
        ToTime(entity.MovingSeconds), entity.AverageSpeedMetresPerSecond, entity.AveragePowerWatts, ToConfidence(entity.Confidence),
        entity.Warnings, entity.StrategyAlgorithmVersion, entity.CreatedAt, entity.CompletedAt);

    private static PredictionAdjustmentDetail ToDetail(PredictionAdjustmentEntity entity) => new(
        entity.Id, entity.PredictionId, Enum.Parse<PacingStrategyType>(entity.StrategyType), entity.StrategyJson, Enum.Parse<AdjustmentState>(entity.State),
        ToTime(entity.MovingSeconds), entity.AverageSpeedMetresPerSecond, entity.AveragePowerWatts, ToConfidence(entity.Confidence),
        entity.Warnings, entity.ResultJson, entity.StrategyAlgorithmVersion, entity.CreatedAt, entity.CompletedAt,
        entity.Segments.OrderBy(segment => segment.Sequence).Select(ToSegment).ToList());

    private static PersistedAdjustmentSegment ToSegment(PredictionAdjustmentSegmentEntity entity) => new(
        entity.Sequence, entity.PowerWatts, entity.SpeedMetresPerSecond, TimeSpan.FromSeconds(entity.SegmentMovingSeconds),
        TimeSpan.FromSeconds(entity.CumulativeMovingSeconds), Enum.Parse<ConfidenceLevel>(entity.Confidence),
        entity.ZoneNumber, entity.StrategyPhase, entity.WPrimeBalanceJoules);

    private static TimeSpan? ToTime(double? seconds) => seconds is null ? null : TimeSpan.FromSeconds(seconds.Value);
    private static ConfidenceLevel? ToConfidence(string? confidence) => confidence is null ? null : Enum.Parse<ConfidenceLevel>(confidence);
}

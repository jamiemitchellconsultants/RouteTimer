using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Adjustments;

public sealed record PredictionAdjustmentSubmission(Guid AdjustmentId, Guid JobId);

/// <summary>
/// Creates one adjustment as a single logical operation: validate the strategy is enabled,
/// canonicalize it, insert the queued child, and enqueue its <c>AdjustPrediction</c> job. If
/// enqueueing fails after the row is already inserted, the row is deleted so no orphaned queued
/// adjustment is left behind without a job that will ever process it.
/// </summary>
public sealed class PredictionAdjustmentService(
    IPredictionAdjustmentRepository adjustments,
    PacingStrategyDispatcher dispatcher,
    IJobQueue jobs,
    TimeProvider timeProvider)
{
    public async Task<PredictionAdjustmentSubmission> CreateAsync(Guid predictionId, PacingStrategyDefinition strategy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        var handler = dispatcher.TryGetHandlerForCreation(strategy.Type)
            ?? throw new PredictionAdjustmentException("pacing-strategy-disabled", $"The {strategy.Type} pacing strategy is disabled.");

        string canonicalJson;
        try
        {
            canonicalJson = handler.Canonicalize(strategy);
        }
        catch (PacingStrategyValidationException exception)
        {
            throw new PredictionAdjustmentException(exception.Code, exception.Message);
        }

        var created = await adjustments.CreateQueuedAsync(
            new QueuedAdjustmentCreation(predictionId, strategy.Type, canonicalJson, timeProvider.GetUtcNow()),
            cancellationToken);

        switch (created.BaselineStatus)
        {
            case AdjustmentBaselineStatus.BaselineNotFound:
                throw new PredictionAdjustmentException("prediction-not-found", "The baseline prediction does not exist.");
            case AdjustmentBaselineStatus.BaselineNotReady:
                throw new PredictionAdjustmentException("adjustment-baseline-not-ready", "The baseline prediction has not succeeded.");
        }

        var adjustmentId = created.AdjustmentId!.Value;
        Guid jobId;
        try
        {
            jobId = await jobs.EnqueueAsync(JobType.AdjustPrediction, adjustmentId, cancellationToken);
        }
        catch
        {
            await adjustments.DeleteAsync(predictionId, adjustmentId, timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }

        return new PredictionAdjustmentSubmission(adjustmentId, jobId);
    }
}

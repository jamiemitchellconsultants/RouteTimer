using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Adjustments;

/// <summary>Cancellation of the adjustment's own active job happens inside <see cref="IPredictionAdjustmentRepository.DeleteAsync"/> itself.</summary>
public sealed class PredictionAdjustmentDeletionService(IPredictionAdjustmentRepository adjustments, TimeProvider timeProvider)
{
    public Task<bool> DeleteAsync(Guid predictionId, Guid adjustmentId, CancellationToken cancellationToken) =>
        adjustments.DeleteAsync(predictionId, adjustmentId, timeProvider.GetUtcNow(), cancellationToken);
}

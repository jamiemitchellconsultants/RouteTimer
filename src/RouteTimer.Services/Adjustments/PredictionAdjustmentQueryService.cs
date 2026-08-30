using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Adjustments;

public sealed class PredictionAdjustmentQueryService(IPredictionAdjustmentRepository adjustments)
{
    public Task<IReadOnlyList<PredictionAdjustmentSummary>> GetSummariesAsync(Guid predictionId, CancellationToken cancellationToken) =>
        adjustments.GetSummariesAsync(predictionId, cancellationToken);

    public Task<PredictionAdjustmentDetail?> GetAsync(Guid predictionId, Guid adjustmentId, CancellationToken cancellationToken) =>
        adjustments.GetAsync(predictionId, adjustmentId, cancellationToken);
}

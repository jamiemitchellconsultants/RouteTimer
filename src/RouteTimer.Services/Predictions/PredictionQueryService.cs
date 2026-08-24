using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Predictions;

public sealed class PredictionQueryService(IPredictionRepository predictions)
{
    public Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken) => predictions.GetSummariesAsync(cancellationToken);
    public Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken) => predictions.GetAsync(predictionId, cancellationToken);
}

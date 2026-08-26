using RouteTimer.Services.Persistence;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Predictions;

public sealed class PredictionQueryService(IPredictionRepository predictions)
{
    public Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken) => predictions.GetSummariesAsync(cancellationToken);
    public Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken) => predictions.GetAsync(predictionId, cancellationToken);
    public Task<PredictionGpxSource?> GetGpxSourceAsync(Guid predictionId, CancellationToken cancellationToken) => predictions.GetGpxSourceAsync(predictionId, cancellationToken);
}

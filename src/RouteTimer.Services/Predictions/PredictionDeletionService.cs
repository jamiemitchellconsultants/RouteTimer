using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Predictions;

public sealed class PredictionDeletionService(
    IPredictionRepository predictions,
    TimeProvider timeProvider)
{
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
        predictions.DeleteAsync(id, timeProvider.GetUtcNow(), ct);
}

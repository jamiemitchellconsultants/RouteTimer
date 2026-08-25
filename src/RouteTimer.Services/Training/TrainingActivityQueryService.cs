using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Training;

public sealed class TrainingActivityQueryService(ITrainingActivityRepository activities)
{
    public Task<IReadOnlyList<TrainingActivitySummary>> GetSummariesAsync(CancellationToken ct) =>
        activities.GetSummariesAsync(ct);

    public Task<TrainingActivityDetail?> GetAsync(Guid id, CancellationToken ct) =>
        activities.GetDetailAsync(id, ct);
}

using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Training;

public sealed record TrainingActivityDeletionResult(bool Deleted, Guid? RebuildJobId);

public sealed class TrainingActivityDeletionService(
    ITrainingActivityRepository activities,
    IJobQueue jobs)
{
    public async Task<TrainingActivityDeletionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (!await activities.DeleteAsync(id, ct))
        {
            return new TrainingActivityDeletionResult(false, null);
        }

        var jobId = await jobs.EnqueueIfNotPendingAsync(JobType.BuildModel, ModelSubject.Id, ct);
        return new TrainingActivityDeletionResult(true, jobId);
    }
}

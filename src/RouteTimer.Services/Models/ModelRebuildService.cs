using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Models;

public sealed class ModelRebuildService(
    IProfileRepository profiles,
    ITrainingActivityRepository activities,
    IJobQueue jobs)
{
    public async Task<Guid> RequestAsync(CancellationToken cancellationToken)
    {
        if (await profiles.GetAsync(cancellationToken) is null)
        {
            throw new ModelRebuildRequestException("profile-required", "A rider profile is required before a model can be built.");
        }

        var counts = await activities.GetCountsAsync(cancellationToken);
        if (counts.Eligible == 0)
        {
            throw new ModelRebuildRequestException("no-eligible-activities", "At least one eligible training activity is required before a model can be built.");
        }

        return await jobs.EnqueueIfNotPendingAsync(JobType.BuildModel, ModelSubject.Id, cancellationToken);
    }
}

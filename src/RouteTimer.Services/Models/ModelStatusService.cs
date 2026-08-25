using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Models;

public sealed class ModelStatusService(
    IProfileRepository profiles,
    ITrainingActivityRepository activities,
    IRiderModelRepository models,
    IJobRepository jobs)
{
    public async Task<ModelStatusResult> GetAsync(CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(cancellationToken);
        var counts = await activities.GetCountsAsync(cancellationToken);
        var latestBuild = await jobs.GetLatestAsync(JobType.BuildModel, ModelSubject.Id, cancellationToken);

        RiderModelSnapshot? currentModel;
        try
        {
            currentModel = await models.GetCurrentAsync(cancellationToken);
        }
        catch (InvalidPersistedRiderModelException)
        {
            return new ModelStatusResult(false, "invalid-rider-model", null, latestBuild);
        }

        if (currentModel is not null)
        {
            return new ModelStatusResult(true, null, currentModel, IsBuildStatus(latestBuild) ? latestBuild : null);
        }

        if (profile is null)
        {
            return new ModelStatusResult(false, "profile-required", null, latestBuild);
        }

        if (counts.Eligible == 0)
        {
            return new ModelStatusResult(false, "no-eligible-activities", null, latestBuild);
        }

        return latestBuild?.State switch
        {
            JobState.Queued or JobState.Running => new ModelStatusResult(false, "model-building", null, latestBuild),
            JobState.Failed => new ModelStatusResult(false, "model-build-failed", null, latestBuild),
            _ => new ModelStatusResult(false, "model-not-ready", null, latestBuild)
        };
    }

    private static bool IsBuildStatus(AnalysisJob? job) =>
        job?.State is JobState.Queued or JobState.Running or JobState.Failed;
}

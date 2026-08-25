using RouteTimer.Contracts.Jobs;

namespace RouteTimer.Client.Jobs;

using RouteTimer.Client.Api;

public enum JobPollOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    Removed
}

public sealed class JobPoller(IRouteTimerApiClient api, TimeProvider timeProvider)
{
    public async Task<JobPollOutcome> PollAsync(
        Guid jobId,
        Func<JobResponse, Task> onUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);

        var consecutiveMissing = 0;

        while (true)
        {
            var job = await api.GetJobAsync(jobId, cancellationToken);
            if (job is null)
            {
                consecutiveMissing++;
                if (consecutiveMissing >= 2)
                {
                    return JobPollOutcome.Removed;
                }
            }
            else
            {
                consecutiveMissing = 0;
                await onUpdate(job);

                if (job.State.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    return JobPollOutcome.Succeeded;
                }

                if (job.State.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    return JobPollOutcome.Failed;
                }

                if (job.State.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return JobPollOutcome.Cancelled;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken);
        }
    }
}

using RouteTimer.Domain.Jobs;

namespace RouteTimer.Services.Jobs;

public interface IJobHandler
{
    JobType Handles { get; }

    Task HandleAsync(AnalysisJob job, CancellationToken cancellationToken);
}

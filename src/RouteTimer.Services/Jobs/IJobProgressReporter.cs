using RouteTimer.Domain.Jobs;

namespace RouteTimer.Services.Jobs;

public interface IJobProgressReporter
{
    Task ReportAsync(AnalysisJob job, int progressPercent, string stage, CancellationToken cancellationToken);
}

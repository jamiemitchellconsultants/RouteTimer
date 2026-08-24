using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Models;

public interface IDescentLimitBuilder
{
    DescentLimitModel Build(IReadOnlyList<CleanedActivity> activities);
}

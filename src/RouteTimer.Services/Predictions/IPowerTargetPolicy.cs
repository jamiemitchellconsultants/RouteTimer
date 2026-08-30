using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;

namespace RouteTimer.Services.Predictions;

public interface IPowerTargetPolicy
{
    PowerEstimate Resolve(PowerTargetContext context);
}

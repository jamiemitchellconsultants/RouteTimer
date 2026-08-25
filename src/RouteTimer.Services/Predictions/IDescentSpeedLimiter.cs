using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Predictions;

public interface IDescentSpeedLimiter
{
    DescentLimitEstimate Resolve(
        double gradient,
        double curvaturePerMetre,
        DescentLimitModel model);
}

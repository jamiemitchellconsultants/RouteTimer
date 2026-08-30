using System;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments.TimeTarget;

public sealed class TimeTargetPowerPolicy(
    double climbScale,
    double otherScale) : IPowerTargetPolicy
{
    public PowerEstimate Resolve(PowerTargetContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        double selectedScale = context.Segment.Gradient >= 0.03 ? climbScale : otherScale;
        return context.BaselineEstimate with { Watts = context.BaselineEstimate.Watts * selectedScale };
    }
}

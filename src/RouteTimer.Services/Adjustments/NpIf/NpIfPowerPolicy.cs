using System;
using RouteTimer.Domain.Adjustments.NpIf;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments.NpIf;

public sealed class NpIfPowerPolicy(
    NpIfScalingMode mode,
    double parameter) : IPowerTargetPolicy
{
    public PowerEstimate Resolve(PowerTargetContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        double watts = mode == NpIfScalingMode.Proportional
            ? context.BaselineEstimate.Watts * parameter
            : context.BaselineEstimate.Watts + parameter;

        return context.BaselineEstimate with { Watts = Math.Max(0, watts) };
    }
}

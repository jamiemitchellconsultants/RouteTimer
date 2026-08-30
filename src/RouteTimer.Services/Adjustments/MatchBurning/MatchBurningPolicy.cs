using System;
using RouteTimer.Domain.Adjustments.MatchBurning;
using RouteTimer.Domain.Adjustments.Zones;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Adjustments.Zones;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments.MatchBurning;

public sealed class MatchBurningPolicy(
    MatchBurningDefinition definition,
    ResolvedMatchCapacity capacity,
    MatchPhasePlan plan,
    ResolvedPowerZoneSet cpAnchoredZones) : IPowerTargetPolicy
{
    public PowerEstimate Resolve(PowerTargetContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (plan.BySequence.TryGetValue(context.Segment.Sequence, out var assignment))
        {
            switch (assignment.Phase)
            {
                case MatchPhase.Burn:
                    if (assignment.BurnWindowIndex.HasValue && assignment.BurnWindowIndex.Value < definition.Windows.Count)
                    {
                        var win = definition.Windows[assignment.BurnWindowIndex.Value];
                        double targetWatts = ResolveIntensityWatts(win, capacity.CriticalPowerWatts, cpAnchoredZones);
                        return context.BaselineEstimate with { Watts = targetWatts };
                    }
                    break;
                case MatchPhase.Conservation:
                    return context.BaselineEstimate with { Watts = capacity.CriticalPowerWatts * definition.ConservationTargetCpFraction };
                case MatchPhase.Recovery:
                    return context.BaselineEstimate with { Watts = capacity.CriticalPowerWatts * definition.RecoveryTargetCpFraction };
                case MatchPhase.Baseline:
                    break;
            }
        }

        return context.BaselineEstimate;
    }

    private static double ResolveIntensityWatts(MatchBurnWindow win, double cpWatts, ResolvedPowerZoneSet cpAnchoredZones)
    {
        if (win.AbsoluteWatts.HasValue) return win.AbsoluteWatts.Value;
        if (win.PercentCp.HasValue) return cpWatts * win.PercentCp.Value;
        if (win.CpZone.HasValue)
        {
            var zone = cpAnchoredZones.Zones[win.CpZone.Value - 1];
            return zone.MidpointTargetWatts;
        }
        throw new InvalidOperationException("Window has no specified intensity.");
    }
}

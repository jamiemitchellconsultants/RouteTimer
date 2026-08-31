using System;
using System.Collections.Generic;
using RouteTimer.Domain.Adjustments.Zones;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments.Zones;

public sealed class ZoneShiftPolicy : IPowerTargetPolicy
{
    private readonly ZoneShiftDefinition _definition;
    private readonly ResolvedPowerZoneSet _zones;
    private readonly int[] _matchCounts;
    private readonly int[] _matchOrder;
    private readonly Dictionary<int, int> _assignedZonesBySequence = new();

    public ZoneShiftPolicy(ZoneShiftDefinition definition, ResolvedPowerZoneSet zones)
    {
        _definition = definition;
        _zones = zones;
        _matchCounts = new int[definition.Assignments.Count];
        _matchOrder = definition.MatchOrder.ToArray();
    }

    public IReadOnlyList<int> MatchCounts => _matchCounts;
    public IReadOnlyDictionary<int, int> AssignedZonesBySequence => _assignedZonesBySequence;
    public bool UsedCappedZoneSevenTarget { get; private set; }

    public PowerEstimate Resolve(PowerTargetContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        foreach (int i in _matchOrder)
        {
            var assignment = _definition.Assignments[i];
            if (assignment.Matches(context.Segment.Gradient))
            {
                _matchCounts[i]++;
                _assignedZonesBySequence[context.Segment.Sequence] = assignment.Zone;

                var resolvedZone = _zones.Zones[assignment.Zone - 1];
                if (assignment.Zone == 7 && assignment.Placement == ZonePlacement.UpperBound)
                {
                    UsedCappedZoneSevenTarget = true;
                }

                double targetWatts = PowerZoneResolver.SelectTarget(resolvedZone, assignment.Placement);
                return context.BaselineEstimate with { Watts = targetWatts };
            }
        }

        return context.BaselineEstimate;
    }
}

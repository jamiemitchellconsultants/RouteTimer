using System;
using System.Collections.Generic;

namespace RouteTimer.Services.Adjustments;

public static class NormalizedPowerCalculator
{
    public static double CalculateNormalizedPower(IReadOnlyList<double> segmentPowers, IReadOnlyList<double> segmentDurations)
    {
        if (segmentPowers is null || segmentDurations is null)
        {
            throw new ArgumentNullException(segmentPowers is null ? nameof(segmentPowers) : nameof(segmentDurations));
        }

        if (segmentPowers.Count != segmentDurations.Count || segmentPowers.Count == 0)
        {
            throw new ArgumentException("Powers and durations must have equal non-zero length.");
        }

        double totalDuration = 0;
        double weightedPowerSum = 0;

        for (int i = 0; i < segmentPowers.Count; i++)
        {
            var p = segmentPowers[i];
            var d = segmentDurations[i];

            if (p < 0 || double.IsNaN(p) || double.IsInfinity(p))
            {
                throw new ArgumentOutOfRangeException(nameof(segmentPowers), "Power values must be non-negative finite numbers.");
            }

            if (d <= 0 || double.IsNaN(d) || double.IsInfinity(d))
            {
                throw new ArgumentOutOfRangeException(nameof(segmentDurations), "Duration values must be positive finite numbers.");
            }

            totalDuration += d;
            weightedPowerSum += Math.Pow(p, 4) * d;
        }

        if (totalDuration <= 0)
        {
            return 0;
        }

        return Math.Pow(weightedPowerSum / totalDuration, 0.25);
    }
}

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
        double durationWeightedPowerSum = 0;

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
            durationWeightedPowerSum += p * d;
        }

        if (totalDuration <= 0)
        {
            return 0;
        }

        // Short-route fallback: routes under 30 seconds fall back to duration-weighted mean power.
        if (totalDuration < 30.0)
        {
            return durationWeightedPowerSum / totalDuration;
        }

        int totalSeconds = (int)Math.Round(totalDuration);
        if (totalSeconds < 30)
        {
            return durationWeightedPowerSum / totalDuration;
        }

        // Expand into 1-second buckets
        double[] secondPowers = new double[totalSeconds];
        int targetIdx = 0;
        double accumulatedTargetSeconds = 0;

        for (int i = 0; i < segmentPowers.Count; i++)
        {
            double segPower = segmentPowers[i];
            double segDuration = segmentDurations[i];
            accumulatedTargetSeconds += segDuration;

            int endIdx = Math.Min(totalSeconds, (int)Math.Round(accumulatedTargetSeconds));
            for (; targetIdx < endIdx; targetIdx++)
            {
                secondPowers[targetIdx] = segPower;
            }
        }

        while (targetIdx < totalSeconds)
        {
            secondPowers[targetIdx] = segmentPowers[^1];
            targetIdx++;
        }

        // Standard Coggan Normalized Power:
        // Reconstruct 1-second buckets and compute trailing 30-second average power P30 for each
        // 30-second window (starting at t = 29, second 30).
        double p30FourthSum = 0;
        double windowSum = 0;

        for (int t = 0; t < 30; t++)
        {
            windowSum += secondPowers[t];
        }

        int p30Count = totalSeconds - 30 + 1;
        p30FourthSum += Math.Pow(windowSum / 30.0, 4);

        for (int t = 30; t < totalSeconds; t++)
        {
            windowSum += secondPowers[t] - secondPowers[t - 30];
            p30FourthSum += Math.Pow(windowSum / 30.0, 4);
        }

        return Math.Pow(p30FourthSum / p30Count, 0.25);
    }
}

using System;
using System.Collections.Generic;

namespace RouteTimer.Services.Adjustments;

public sealed record PacingSearchOptions(
    double MinPowerMultiplier,
    double MaxPowerMultiplier,
    int CoarseGridPointCount = 9,
    double ConvergenceToleranceSeconds = 30.0,
    int MaximumEvaluations = 40);

public static class BoundedPacingSearch
{
    public static double FindMultiplier(
        double minMultiplier,
        double maxMultiplier,
        double targetValue,
        Func<double, double> evaluate,
        double tolerance = 30.0,
        int maximumEvaluations = 40)
    {
        if (evaluate is null) throw new ArgumentNullException(nameof(evaluate));
        if (minMultiplier >= maxMultiplier) throw new ArgumentException("Min multiplier must be less than max multiplier.");

        int evaluationCount = 0;
        var cache = new Dictionary<double, double>();

        double EvaluateCached(double m)
        {
            double key = Math.Round(m, 8);
            if (cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (evaluationCount >= maximumEvaluations)
            {
                return double.NaN;
            }

            evaluationCount++;
            double val = evaluate(m);
            cache[key] = val;
            return val;
        }

        const int gridPoints = 9;
        double step = (maxMultiplier - minMultiplier) / (gridPoints - 1);

        double bestMultiplier = minMultiplier;
        double bestDiff = double.MaxValue;

        var evaluatedGrid = new List<(double multiplier, double value, double diff)>();

        for (int i = 0; i < gridPoints; i++)
        {
            if (evaluationCount >= maximumEvaluations) break;

            double m = minMultiplier + i * step;
            double val = EvaluateCached(m);

            if (!double.IsNaN(val))
            {
                double diff = Math.Abs(val - targetValue);
                evaluatedGrid.Add((m, val, diff));

                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestMultiplier = m;
                }

                if (diff <= tolerance)
                {
                    return m;
                }
            }
        }

        if (evaluatedGrid.Count == 0)
        {
            return bestMultiplier;
        }

        // Search adjacent sign-changing bracket or interval around best candidate
        double low = minMultiplier;
        double high = maxMultiplier;

        for (int i = 0; i < evaluatedGrid.Count - 1; i++)
        {
            var p1 = evaluatedGrid[i];
            var p2 = evaluatedGrid[i + 1];

            double d1 = p1.value - targetValue;
            double d2 = p2.value - targetValue;

            if ((d1 <= 0 && d2 >= 0) || (d1 >= 0 && d2 <= 0))
            {
                low = p1.multiplier;
                high = p2.multiplier;
                break;
            }
        }

        if (low == minMultiplier && high == maxMultiplier)
        {
            // Fallback: bracket around best point
            low = Math.Max(minMultiplier, bestMultiplier - step);
            high = Math.Min(maxMultiplier, bestMultiplier + step);
        }

        // Bisection phase until tolerance met or max evaluations reached
        while (evaluationCount < maximumEvaluations && Math.Abs(high - low) > 1e-6)
        {
            double mid = (low + high) / 2.0;
            double midVal = EvaluateCached(mid);

            if (double.IsNaN(midVal))
            {
                break;
            }

            double midDiff = Math.Abs(midVal - targetValue);
            if (midDiff < bestDiff)
            {
                bestDiff = midDiff;
                bestMultiplier = mid;
            }

            if (midDiff <= tolerance)
            {
                return mid;
            }

            double lowVal = cache.TryGetValue(Math.Round(low, 8), out var lv) ? lv : EvaluateCached(low);
            if (double.IsNaN(lowVal)) break;

            if ((midVal - targetValue) * (lowVal - targetValue) <= 0)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        return bestMultiplier;
    }
}

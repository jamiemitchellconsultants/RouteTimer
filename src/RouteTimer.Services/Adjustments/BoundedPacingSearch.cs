using System;

namespace RouteTimer.Services.Adjustments;

public static class BoundedPacingSearch
{
    public const int GridSteps = 20;
    public const int MaximumBisectionIterations = 30;

    public static double FindMultiplier(
        double minMultiplier,
        double maxMultiplier,
        double targetValue,
        Func<double, double> evaluate,
        double tolerance = 0.001)
    {
        if (evaluate is null) throw new ArgumentNullException(nameof(evaluate));
        if (minMultiplier >= maxMultiplier) throw new ArgumentException("Min multiplier must be less than max multiplier.");

        double bestDist = double.MaxValue;
        double bestPoint = minMultiplier;

        double step = (maxMultiplier - minMultiplier) / GridSteps;
        for (int i = 0; i <= GridSteps; i++)
        {
            double m = minMultiplier + i * step;
            double val = evaluate(m);
            double dist = Math.Abs(val - targetValue);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestPoint = m;
            }
        }

        double low = Math.Max(minMultiplier, bestPoint - step);
        double high = Math.Min(maxMultiplier, bestPoint + step);

        for (int iter = 0; iter < MaximumBisectionIterations; iter++)
        {
            double mid = (low + high) / 2.0;
            double midVal = evaluate(mid);

            if (Math.Abs(midVal - targetValue) <= tolerance)
            {
                return mid;
            }

            double lowVal = evaluate(low);
            double highVal = evaluate(high);

            if ((midVal - targetValue) * (lowVal - targetValue) <= 0)
            {
                high = mid;
            }
            else if ((midVal - targetValue) * (highVal - targetValue) <= 0)
            {
                low = mid;
            }
            else
            {
                double distLow = Math.Abs(lowVal - targetValue);
                double distHigh = Math.Abs(highVal - targetValue);
                if (distLow < distHigh) high = mid;
                else low = mid;
            }

            if (Math.Abs(high - low) < 1e-7)
            {
                return mid;
            }
        }

        return (low + high) / 2.0;
    }
}

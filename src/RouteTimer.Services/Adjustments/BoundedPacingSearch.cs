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
        int bestIndex = 0;

        double step = (maxMultiplier - minMultiplier) / GridSteps;
        var gridPoints = new double[GridSteps + 1];
        var gridValues = new double[GridSteps + 1];
        for (int i = 0; i <= GridSteps; i++)
        {
            gridPoints[i] = minMultiplier + i * step;
            gridValues[i] = evaluate(gridPoints[i]);
            double dist = Math.Abs(gridValues[i] - targetValue);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        // The bracket endpoints are the grid neighbours of the best grid point, so their values are
        // already known. Each endpoint then keeps its evaluated value as the bracket narrows: only
        // one of them moves per iteration, and evaluate() is a full route simulation for every real
        // caller.
        int lowIndex = Math.Max(0, bestIndex - 1);
        int highIndex = Math.Min(GridSteps, bestIndex + 1);
        double low = gridPoints[lowIndex];
        double high = gridPoints[highIndex];
        double lowVal = gridValues[lowIndex];
        double highVal = gridValues[highIndex];

        for (int iter = 0; iter < MaximumBisectionIterations; iter++)
        {
            double mid = (low + high) / 2.0;
            double midVal = evaluate(mid);

            if (Math.Abs(midVal - targetValue) <= tolerance)
            {
                return mid;
            }

            bool moveHigh;
            if ((midVal - targetValue) * (lowVal - targetValue) <= 0)
            {
                moveHigh = true;
            }
            else if ((midVal - targetValue) * (highVal - targetValue) <= 0)
            {
                moveHigh = false;
            }
            else
            {
                moveHigh = Math.Abs(lowVal - targetValue) < Math.Abs(highVal - targetValue);
            }

            if (moveHigh)
            {
                high = mid;
                highVal = midVal;
            }
            else
            {
                low = mid;
                lowVal = midVal;
            }

            if (Math.Abs(high - low) < 1e-7)
            {
                return mid;
            }
        }

        return (low + high) / 2.0;
    }
}

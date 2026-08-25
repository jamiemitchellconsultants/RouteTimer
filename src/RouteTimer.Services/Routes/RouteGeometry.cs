using RouteTimer.Domain.Routes;

namespace RouteTimer.Services.Routes;

public readonly record struct GeometryValue(
    double SmoothedElevationMetres,
    double Gradient,
    double CurvaturePerMetre);

public static class RouteGeometry
{
    public static IReadOnlyList<double> CumulativeDistances(IReadOnlyList<GeoPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        ValidatePoints(points);

        var cumulative = new double[points.Count];
        for (var index = 1; index < points.Count; index++)
        {
            cumulative[index] = cumulative[index - 1] + GeoMath.DistanceMetres(points[index - 1], points[index]);
            if (!double.IsFinite(cumulative[index]))
            {
                throw new ArgumentException("Route distances must be finite values.", nameof(points));
            }
        }

        return cumulative;
    }

    public static IReadOnlyList<GeometryValue> Enrich(
        IReadOnlyList<GeoPoint> points,
        IReadOnlyList<double> cumulativeDistances,
        double elevationWindowMetres)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(cumulativeDistances);
        if (points.Count == 0 || points.Count != cumulativeDistances.Count || !double.IsFinite(elevationWindowMetres) || elevationWindowMetres <= 0)
        {
            throw new ArgumentException("Geometry requires equal non-empty point and distance collections and a positive finite elevation window.");
        }

        ValidatePoints(points);
        ValidateDistances(cumulativeDistances);

        var smoothedElevations = new double[points.Count];
        var halfWindow = elevationWindowMetres / 2;
        for (var target = 0; target < points.Count; target++)
        {
            var targetDistance = cumulativeDistances[target];
            var selected = Enumerable.Range(0, points.Count)
                .Where(index => cumulativeDistances[index] >= targetDistance - halfWindow && cumulativeDistances[index] <= targetDistance + halfWindow)
                .ToArray();
            var line = FitLine(points, cumulativeDistances, selected, null);

            for (var iteration = 0; iteration < 3; iteration++)
            {
                var residuals = selected
                    .Select(index => Math.Abs(points[index].ElevationMetres - line.Evaluate(cumulativeDistances[index])))
                    .ToArray();
                var scale = Median(residuals);
                if (scale <= 1e-9)
                {
                    break;
                }

                var threshold = 1.345 * scale;
                line = FitLine(points, cumulativeDistances, selected, index =>
                {
                    var residual = Math.Abs(points[index].ElevationMetres - line.Evaluate(cumulativeDistances[index]));
                    return residual <= threshold ? 1 : threshold / residual;
                });
            }

            smoothedElevations[target] = line.Evaluate(targetDistance);
        }

        var values = new GeometryValue[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            values[index] = new GeometryValue(smoothedElevations[index], GradientAt(cumulativeDistances, smoothedElevations, index), CurvatureAt(points, cumulativeDistances, index));
        }

        return values;
    }

    private static void ValidatePoints(IReadOnlyList<GeoPoint> points)
    {
        if (points.Count == 0 || points.Any(point => !double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude) || !double.IsFinite(point.ElevationMetres)))
        {
            throw new ArgumentException("Route coordinates and elevation must be finite values.", nameof(points));
        }
    }

    private static void ValidateDistances(IReadOnlyList<double> distances)
    {
        if (distances.Any(distance => !double.IsFinite(distance)) || distances[0] != 0 || distances.Zip(distances.Skip(1), (first, second) => second < first).Any(isDecreasing => isDecreasing))
        {
            throw new ArgumentException("Cumulative distances must be finite, start at zero, and not decrease.", nameof(distances));
        }
    }

    private static Line FitLine(IReadOnlyList<GeoPoint> points, IReadOnlyList<double> distances, IReadOnlyList<int> selected, Func<int, double>? weightForIndex)
    {
        var weights = selected.Select(index => weightForIndex?.Invoke(index) ?? 1).ToArray();
        var totalWeight = weights.Sum();
        var meanX = selected.Select((index, position) => distances[index] * weights[position]).Sum() / totalWeight;
        var meanY = selected.Select((index, position) => points[index].ElevationMetres * weights[position]).Sum() / totalWeight;
        var denominator = selected.Select((index, position) => weights[position] * Math.Pow(distances[index] - meanX, 2)).Sum();
        if (denominator <= double.Epsilon)
        {
            return new Line(0, meanY);
        }

        var slope = selected.Select((index, position) => weights[position] * (distances[index] - meanX) * (points[index].ElevationMetres - meanY)).Sum() / denominator;
        return new Line(slope, meanY - (slope * meanX));
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }

    private static double GradientAt(IReadOnlyList<double> distances, IReadOnlyList<double> elevations, int index)
    {
        var first = index == 0 ? 0 : index - 1;
        var last = index == elevations.Count - 1 ? elevations.Count - 1 : index + 1;
        var run = distances[last] - distances[first];
        return run <= 0 ? 0 : (elevations[last] - elevations[first]) / run;
    }

    private static double CurvatureAt(IReadOnlyList<GeoPoint> points, IReadOnlyList<double> distances, int index)
    {
        if (index == 0 || index == points.Count - 1)
        {
            return 0;
        }

        var run = distances[index + 1] - distances[index - 1];
        if (run <= 0)
        {
            return 0;
        }

        var firstHeading = GeoMath.HeadingRadians(points[index - 1], points[index]);
        var secondHeading = GeoMath.HeadingRadians(points[index], points[index + 1]);
        return Math.Abs(GeoMath.NormalizeRadians(secondHeading - firstHeading)) / run;
    }

    private readonly record struct Line(double Slope, double Intercept)
    {
        public double Evaluate(double x) => Intercept + (Slope * x);
    }
}

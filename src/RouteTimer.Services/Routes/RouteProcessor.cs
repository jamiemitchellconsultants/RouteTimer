using RouteTimer.Domain.Routes;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Routes;

public sealed class RouteProcessor(RouteProcessingOptions options) : IRouteProcessor
{
    public ProcessedRoute Process(IReadOnlyList<GeoPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var continuousPoints = RemoveAdjacentDuplicates(points);
        if (continuousPoints.Count < 2)
        {
            throw new RouteInputException("A route requires at least two distinct points.");
        }

        var cumulative = BuildCumulativeDistances(continuousPoints);
        var totalDistance = cumulative[^1];
        if (!double.IsFinite(totalDistance) || totalDistance < options.SegmentMetres)
        {
            throw new RouteInputException("A route requires at least one full segment of distance.");
        }

        var distances = BuildSampleDistances(totalDistance);
        var rawPoints = distances.Select(distance => InterpolateAtDistance(continuousPoints, cumulative, distance)).ToList();
        var smoothedElevations = distances.Select((distance, index) => SmoothedElevation(distances, rawPoints, index, distance)).ToList();
        var samples = new List<RouteSample>(distances.Count);

        for (var index = 0; index < distances.Count; index++)
        {
            var segmentDistance = index == 0 ? 0 : distances[index] - distances[index - 1];
            var gradient = GradientAt(distances, smoothedElevations, index);
            var curvature = CurvatureAt(rawPoints, distances, index);
            var point = rawPoints[index] with { ElevationMetres = smoothedElevations[index] };
            samples.Add(new RouteSample(index, point, distances[index], segmentDistance, gradient, curvature));
        }

        var ascent = samples.Zip(samples.Skip(1), (first, second) => Math.Max(0, second.Point.ElevationMetres - first.Point.ElevationMetres)).Sum();
        return new ProcessedRoute(samples, totalDistance, ascent);
    }

    private static List<GeoPoint> RemoveAdjacentDuplicates(IReadOnlyList<GeoPoint> points)
    {
        var result = new List<GeoPoint>();
        foreach (var point in points)
        {
            if (!double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude) || !double.IsFinite(point.ElevationMetres))
            {
                throw new RouteInputException("Route coordinates and elevation must be finite values.");
            }

            if (result.Count == 0 || GeoMath.DistanceMetres(result[^1], point) > .01)
            {
                result.Add(point);
            }
        }

        return result;
    }

    private static List<double> BuildCumulativeDistances(IReadOnlyList<GeoPoint> points)
    {
        var result = new List<double> { 0 };
        for (var index = 1; index < points.Count; index++)
        {
            result.Add(result[^1] + GeoMath.DistanceMetres(points[index - 1], points[index]));
        }

        return result;
    }

    private List<double> BuildSampleDistances(double totalDistance)
    {
        var result = new List<double>();
        for (var distance = 0d; distance < totalDistance; distance += options.SegmentMetres)
        {
            result.Add(distance);
        }

        if (result.Count == 0 || totalDistance - result[^1] > .01)
        {
            result.Add(totalDistance);
        }

        return result;
    }

    private static GeoPoint InterpolateAtDistance(IReadOnlyList<GeoPoint> points, IReadOnlyList<double> cumulative, double distance)
    {
        var segment = 1;
        while (segment < cumulative.Count && cumulative[segment] < distance)
        {
            segment++;
        }

        if (segment == cumulative.Count)
        {
            return points[^1];
        }

        var lowerDistance = cumulative[segment - 1];
        var span = cumulative[segment] - lowerDistance;
        return GeoMath.Interpolate(points[segment - 1], points[segment], span <= 0 ? 0 : (distance - lowerDistance) / span);
    }

    private double SmoothedElevation(IReadOnlyList<double> distances, IReadOnlyList<GeoPoint> points, int targetIndex, double targetDistance)
    {
        var windowStart = targetDistance - (options.ElevationWindowMetres / 2);
        var windowEnd = targetDistance + (options.ElevationWindowMetres / 2);
        var selected = Enumerable.Range(0, distances.Count)
            .Where(index => distances[index] >= windowStart && distances[index] <= windowEnd)
            .ToList();

        if (selected.Count < 2)
        {
            return points[targetIndex].ElevationMetres;
        }

        var meanX = selected.Average(index => distances[index]);
        var meanY = selected.Average(index => points[index].ElevationMetres);
        var denominator = selected.Sum(index => Math.Pow(distances[index] - meanX, 2));
        if (denominator < double.Epsilon)
        {
            return meanY;
        }

        var slope = selected.Sum(index => (distances[index] - meanX) * (points[index].ElevationMetres - meanY)) / denominator;
        return meanY + (slope * (targetDistance - meanX));
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
}

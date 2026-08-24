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

        var cumulative = RouteGeometry.CumulativeDistances(continuousPoints);
        var totalDistance = cumulative[^1];
        if (!double.IsFinite(totalDistance) || totalDistance < options.SegmentMetres)
        {
            throw new RouteInputException("A route requires at least one full segment of distance.");
        }

        var distances = BuildSampleDistances(totalDistance);
        var rawPoints = distances.Select(distance => InterpolateAtDistance(continuousPoints, cumulative, distance)).ToList();
        var geometry = RouteGeometry.Enrich(rawPoints, distances, options.ElevationWindowMetres);
        var samples = new List<RouteSample>(distances.Count);

        for (var index = 0; index < distances.Count; index++)
        {
            var segmentDistance = index == 0 ? 0 : distances[index] - distances[index - 1];
            var value = geometry[index];
            var point = rawPoints[index] with { ElevationMetres = value.SmoothedElevationMetres };
            samples.Add(new RouteSample(index, point, distances[index], segmentDistance, value.Gradient, value.CurvaturePerMetre));
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

}

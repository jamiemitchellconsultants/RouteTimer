using RouteTimer.Domain.Routes;

namespace RouteTimer.Domain.Predictions;

public sealed record PredictionRoute
{
    private readonly IReadOnlyList<PredictionRouteSegment> _segments;

    public PredictionRoute(IReadOnlyList<PredictionRouteSegment> segments, double distanceMetres, double ascentMetres)
    {
        if (segments is null || segments.Count == 0)
            throw new ArgumentException("Prediction route requires at least one segment.", nameof(segments));

        var ordered = segments.ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var segment = ordered[index] ?? throw new ArgumentException("Prediction route segments must not be null.", nameof(segments));
            var expectedSequence = index == 0 ? segment.Sequence : ordered[index - 1].Sequence + 1;
            if (index > 0 && segment.Sequence != expectedSequence)
                throw new ArgumentException("Prediction route segments must be contiguous.", nameof(segments));
        }

        _segments = Array.AsReadOnly(ordered);
        DistanceMetres = distanceMetres;
        AscentMetres = ascentMetres;
    }

    public IReadOnlyList<PredictionRouteSegment> Segments => _segments;
    public double DistanceMetres { get; }
    public double AscentMetres { get; }

    public static PredictionRoute FromProcessed(ProcessedRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var segments = route.Samples
            .Skip(1)
            .Select(sample => new PredictionRouteSegment(
                sample.Sequence,
                sample.Point.Latitude,
                sample.Point.Longitude,
                sample.Point.ElevationMetres,
                sample.CumulativeDistanceMetres,
                sample.SegmentDistanceMetres,
                sample.Gradient,
                sample.CurvaturePerMetre))
            .ToArray();
        return new PredictionRoute(segments, route.DistanceMetres, route.AscentMetres);
    }
}

public sealed record PredictionRouteSegment(
    int Sequence,
    double Latitude,
    double Longitude,
    double ElevationMetres,
    double CumulativeDistanceMetres,
    double SegmentDistanceMetres,
    double Gradient,
    double CurvaturePerMetre);

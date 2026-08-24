namespace RouteTimer.Domain.Routes;

public sealed record RouteSample(
    int Sequence,
    GeoPoint Point,
    double CumulativeDistanceMetres,
    double SegmentDistanceMetres,
    double Gradient,
    double CurvaturePerMetre);

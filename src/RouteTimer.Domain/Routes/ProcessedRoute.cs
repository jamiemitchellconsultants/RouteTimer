namespace RouteTimer.Domain.Routes;

public sealed record ProcessedRoute(
    IReadOnlyList<RouteSample> Samples,
    double DistanceMetres,
    double AscentMetres);

using RouteTimer.Domain.Routes;

namespace RouteTimer.Services.Routes;

public sealed record ParsedGpxRoute(string Name, IReadOnlyList<GeoPoint> Points);

using RouteTimer.Domain.Routes;

namespace RouteTimer.Services.Routes;

public interface IRouteProcessor
{
    ProcessedRoute Process(IReadOnlyList<GeoPoint> points);
}

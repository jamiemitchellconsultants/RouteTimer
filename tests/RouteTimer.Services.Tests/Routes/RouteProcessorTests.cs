using RouteTimer.Domain.Routes;
using RouteTimer.Services.Routes;
using RouteTimer.Services.Tests.Routes;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Tests.Routes;

public sealed class RouteProcessorTests
{
    [Fact]
    public void Process_resamples_at_25m_and_uses_smoothed_elevation_for_grade()
    {
        var source = RouteFixtures.StraightClimb(lengthMetres: 200, riseMetres: 10, noiseMetres: 2);
        var points = source.Select(point => new GeoPoint(point.Latitude, point.Longitude, point.ElevationMetres)).ToList();

        var route = new RouteProcessor(RouteProcessingOptions.Default).Process(points);

        Assert.InRange(route.Samples.Count, 8, 10);
        Assert.All(route.Samples.Skip(2).Take(4), point => Assert.InRange(point.Gradient, .03, .07));
    }

    [Fact]
    public void Process_rejects_a_route_with_fewer_than_two_distinct_points()
    {
        var processor = new RouteProcessor(RouteProcessingOptions.Default);

        Assert.Throws<RouteInputException>(() => processor.Process([new GeoPoint(51, -2, 50)]));
    }
}

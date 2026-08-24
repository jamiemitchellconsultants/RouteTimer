using RouteTimer.Domain.Routes;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.Routes;

public sealed class RouteGeometryTests
{
    [Fact]
    public void Enrich_derives_hand_checked_gradient_from_a_straight_twenty_five_metre_climb()
    {
        var points = new[]
        {
            new GeoPoint(0, 0, 0),
            new GeoPoint(0, 0.00022483, 2.5),
            new GeoPoint(0, 0.00044966, 5)
        };

        var values = RouteGeometry.Enrich(points, new[] { 0d, 25d, 50d }, 100);

        Assert.Collection(values,
            value => { Assert.Equal(0, value.SmoothedElevationMetres, 10); Assert.Equal(.1, value.Gradient, 10); Assert.Equal(0, value.CurvaturePerMetre, 12); },
            value => { Assert.Equal(2.5, value.SmoothedElevationMetres, 10); Assert.Equal(.1, value.Gradient, 10); Assert.Equal(0, value.CurvaturePerMetre, 12); },
            value => { Assert.Equal(5, value.SmoothedElevationMetres, 10); Assert.Equal(.1, value.Gradient, 10); Assert.Equal(0, value.CurvaturePerMetre, 12); });
    }

    [Fact]
    public void Enrich_derives_hand_checked_right_angle_curvature()
    {
        var points = new[]
        {
            new GeoPoint(0, 0, 0),
            new GeoPoint(0, 0.00022483, 0),
            new GeoPoint(0.00022483, 0.00022483, 0)
        };

        var values = RouteGeometry.Enrich(points, new[] { 0d, 25d, 50d }, 100);

        Assert.Equal(0, values[0].CurvaturePerMetre, 12);
        Assert.Equal(Math.PI / 100, values[1].CurvaturePerMetre, 8);
        Assert.Equal(0, values[2].CurvaturePerMetre, 12);
    }
}
